using System.Globalization;
using System.Text;
using System.Xml;
using BlazorNativeSdk.Devices;

namespace BlazorNativeSdk.WindowsPlatform;

/// <summary>How toast clicks are delivered.</summary>
/// <param name="ProtocolScheme">The app's URL scheme for protocol activation, or <see langword="null"/> for in-process (foreground) activation.</param>
/// <param name="SigningKey">The key that signs protocol URIs (required with <paramref name="ProtocolScheme"/>).</param>
/// <param name="SupportsUrgentScenario">Whether the OS supports <c>scenario="urgent"</c> (Windows 11 build 22546+).</param>
internal sealed record ToastActivationOptions(string? ProtocolScheme, byte[]? SigningKey, bool SupportsUrgentScenario);

/// <summary>Builds toast XML for <see cref="LocalNotification"/> with <see cref="XmlWriter"/> (escaping all content).</summary>
internal static class ToastXmlBuilder
{
    /// <summary>The prefix of text input ids; the action index is appended.</summary>
    public const string ReplyInputIdPrefix = "bns-reply-";

    /// <summary>Windows shows at most five buttons.</summary>
    public const int MaxActions = 5;

    public static string Build(LocalNotification notification, ToastActivationOptions activation)
    {
        ArgumentNullException.ThrowIfNull(notification);
        ArgumentNullException.ThrowIfNull(activation);

        var settings = new XmlWriterSettings
        {
            OmitXmlDeclaration = true,
            ConformanceLevel = ConformanceLevel.Document,
            CheckCharacters = true,
        };

        var output = new StringBuilder();
        using (var writer = XmlWriter.Create(new StringWriter(output, CultureInfo.InvariantCulture), settings))
        {
            writer.WriteStartElement("toast");
            var payload = new ToastActivationPayload(notification.Id, null, notification.Route, notification.Data);
            WriteActivation(writer, "launch", payload, activation, acceptsText: false);
            if (notification.Importance == NotificationImportance.High && activation.SupportsUrgentScenario)
            {
                writer.WriteAttributeString("scenario", "urgent");
            }

            writer.WriteStartElement("visual");
            writer.WriteStartElement("binding");
            writer.WriteAttributeString("template", "ToastGeneric");
            WriteText(writer, notification.Title, placement: null);
            if (!string.IsNullOrEmpty(notification.Body))
            {
                WriteText(writer, notification.Body, placement: null);
            }

            if (!string.IsNullOrEmpty(notification.Subtitle))
            {
                WriteText(writer, notification.Subtitle, placement: "attribution");
            }

            writer.WriteEndElement(); // binding
            writer.WriteEndElement(); // visual

            var actions = notification.Actions.Take(MaxActions).ToArray();
            if (actions.Length > 0)
            {
                writer.WriteStartElement("actions");

                // Inputs must precede the buttons.
                for (var i = 0; i < actions.Length; i++)
                {
                    if (actions[i].AcceptsText)
                    {
                        writer.WriteStartElement("input");
                        writer.WriteAttributeString("id", ReplyInputIdPrefix + i.ToString(CultureInfo.InvariantCulture));
                        writer.WriteAttributeString("type", "text");
                        if (!string.IsNullOrEmpty(actions[i].TextPlaceholder))
                        {
                            writer.WriteAttributeString("placeHolderContent", Sanitize(actions[i].TextPlaceholder!));
                        }

                        writer.WriteEndElement();
                    }
                }

                for (var i = 0; i < actions.Length; i++)
                {
                    var action = actions[i];
                    writer.WriteStartElement("action");
                    writer.WriteAttributeString("content", Sanitize(action.Title));
                    WriteActivation(writer, "arguments", payload with { ActionId = action.Id }, activation, action.AcceptsText);
                    if (action.AcceptsText)
                    {
                        writer.WriteAttributeString("hint-inputId", ReplyInputIdPrefix + i.ToString(CultureInfo.InvariantCulture));
                    }

                    writer.WriteEndElement();
                }

                writer.WriteEndElement(); // actions
            }

            if (notification.Silent || notification.Importance == NotificationImportance.Low)
            {
                writer.WriteStartElement("audio");
                writer.WriteAttributeString("silent", "true");
                writer.WriteEndElement();
            }

            writer.WriteEndElement(); // toast
        }

        return output.ToString();
    }

    /// <summary>Removes characters that XML 1.0 cannot represent (control characters, lone surrogates).</summary>
    internal static string Sanitize(string value)
    {
        var index = 0;
        while (index < value.Length && IsValidAt(value, index, out var width))
        {
            index += width;
        }

        if (index == value.Length)
        {
            return value;
        }

        var builder = new StringBuilder(value.Length);
        builder.Append(value, 0, index);
        while (index < value.Length)
        {
            if (IsValidAt(value, index, out var width))
            {
                builder.Append(value, index, width);
                index += width;
            }
            else
            {
                index++;
            }
        }

        return builder.ToString();
    }

    private static bool IsValidAt(string value, int index, out int width)
    {
        if (index + 1 < value.Length && XmlConvert.IsXmlSurrogatePair(value[index + 1], value[index]))
        {
            width = 2;
            return true;
        }

        width = 1;
        return XmlConvert.IsXmlChar(value[index]);
    }

    private static void WriteText(XmlWriter writer, string text, string? placement)
    {
        writer.WriteStartElement("text");
        if (placement is not null)
        {
            writer.WriteAttributeString("placement", placement);
        }

        writer.WriteString(Sanitize(text));
        writer.WriteEndElement();
    }

    private static void WriteActivation(XmlWriter writer, string attribute, ToastActivationPayload payload, ToastActivationOptions activation, bool acceptsText)
    {
        // Typed replies only reach the app through in-process activation, so text actions always use foreground activation.
        if (activation.ProtocolScheme is { } scheme && activation.SigningKey is { } key && !acceptsText)
        {
            writer.WriteAttributeString(attribute, ToastLaunchArguments.ToProtocolUri(scheme, payload, key).AbsoluteUri);
            writer.WriteAttributeString("activationType", "protocol");
        }
        else
        {
            writer.WriteAttributeString(attribute, ToastLaunchArguments.ToArguments(payload));
            writer.WriteAttributeString("activationType", "foreground");
        }
    }
}
