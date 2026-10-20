namespace BlazorNativeSdk.Desktop;

/// <summary>A registered global shortcut. Dispose to unregister.</summary>
public interface IGlobalShortcutRegistration : IAsyncDisposable
{
    /// <summary>The gesture as registered (after resolving <see cref="KeyModifiers.CommandOrControl"/>).</summary>
    KeyGesture Gesture { get; }
}

/// <summary>Registers system-wide keyboard shortcuts that work while the app is not focused.</summary>
/// <remarks>
/// Registration fails with <see cref="NativeErrorCodes.Conflict"/> when another application owns the gesture.
/// Wayland compositors and sandboxed macOS apps may not allow global shortcuts at all; check the capability status.
/// </remarks>
public interface IGlobalShortcutService : INativeCapability
{
    /// <summary>Registers a shortcut. The callback runs on a background or UI thread depending on the adapter.</summary>
    ValueTask<NativeResult<IGlobalShortcutRegistration>> RegisterAsync(KeyGesture gesture, Func<ValueTask> callback, CancellationToken cancellationToken = default);

    /// <summary>The currently registered gestures.</summary>
    IReadOnlyList<KeyGesture> RegisteredGestures { get; }
}

/// <summary>The kind of a menu item.</summary>
public enum NativeMenuItemKind
{
    /// <summary>A regular item.</summary>
    Normal,

    /// <summary>A checkable item.</summary>
    Checkbox,

    /// <summary>A radio item; items with the same <see cref="NativeMenuItem.RadioGroup"/> are exclusive.</summary>
    Radio,

    /// <summary>A separator line.</summary>
    Separator,

    /// <summary>An item with children.</summary>
    Submenu,
}

/// <summary>Standard menu roles that adapters map to native behavior (for example macOS standard selectors).</summary>
public enum NativeMenuRole
{
    /// <summary>No role; the item invokes its callback.</summary>
    None,
#pragma warning disable CS1591 // Role names are self-describing.
    About, Preferences, Quit, Hide, HideOthers, ShowAll,
    Undo, Redo, Cut, Copy, Paste, SelectAll,
    Minimize, Zoom, Close, ToggleFullScreen, WindowMenu, HelpMenu,
#pragma warning restore CS1591
}

/// <summary>A native menu item.</summary>
public sealed record NativeMenuItem
{
    /// <summary>An id reported by <see cref="INativeMenuService.ItemInvoked"/> and context menu results.</summary>
    public string Id { get; init; } = Guid.NewGuid().ToString("N");

    /// <summary>The label. Use <c>&amp;</c> to mark an access key on Windows.</summary>
    public string? Label { get; init; }

    /// <summary>The kind.</summary>
    public NativeMenuItemKind Kind { get; init; }

    /// <summary>A keyboard accelerator displayed next to the label (window menus) or registered with the application menu.</summary>
    public KeyGesture? Accelerator { get; init; }

    /// <summary>Whether the item can be invoked.</summary>
    public bool IsEnabled { get; init; } = true;

    /// <summary>The checked state of checkbox and radio items.</summary>
    public bool IsChecked { get; init; }

    /// <summary>The radio group name.</summary>
    public string? RadioGroup { get; init; }

    /// <summary>A standard role.</summary>
    public NativeMenuRole Role { get; init; }

    /// <summary>Child items of a submenu.</summary>
    public IReadOnlyList<NativeMenuItem> Items { get; init; } = [];

    /// <summary>A callback invoked when the item is chosen.</summary>
    public Func<ValueTask>? OnInvoked { get; init; }

    /// <summary>Creates a separator.</summary>
    public static NativeMenuItem Separator() => new() { Kind = NativeMenuItemKind.Separator };

    /// <summary>Creates a regular item.</summary>
    public static NativeMenuItem Create(string id, string label, Func<ValueTask>? onInvoked = null, string? accelerator = null) => new()
    {
        Id = id,
        Label = label,
        OnInvoked = onInvoked,
        Accelerator = accelerator is null ? null : KeyGesture.Parse(accelerator),
    };

    /// <summary>Creates a submenu.</summary>
    public static NativeMenuItem Submenu(string label, params NativeMenuItem[] items) => new()
    {
        Label = label,
        Kind = NativeMenuItemKind.Submenu,
        Items = items,
    };

    /// <summary>Creates an item with a standard role.</summary>
    public static NativeMenuItem ForRole(NativeMenuRole role, string? label = null) => new()
    {
        Id = "role." + role.ToString().ToLowerInvariant(),
        Label = label,
        Role = role,
    };
}

/// <summary>A native menu.</summary>
/// <param name="Items">The top-level items.</param>
public sealed record NativeMenu(IReadOnlyList<NativeMenuItem> Items)
{
    /// <summary>Creates a menu.</summary>
    public static NativeMenu Create(params NativeMenuItem[] items) => new(items);

    /// <summary>Finds an item by id, searching submenus.</summary>
    public NativeMenuItem? Find(string id) => Find(Items, id);

    private static NativeMenuItem? Find(IReadOnlyList<NativeMenuItem> items, string id)
    {
        foreach (var item in items)
        {
            if (item.Id == id)
            {
                return item;
            }

            if (item.Items.Count > 0 && Find(item.Items, id) is { } child)
            {
                return child;
            }
        }

        return null;
    }
}

/// <summary>Event data for an invoked menu item.</summary>
public sealed class MenuItemInvokedEventArgs : EventArgs
{
    /// <summary>Creates event data.</summary>
    public MenuItemInvokedEventArgs(NativeMenuItem item, string? windowId)
    {
        Item = item;
        WindowId = windowId;
    }

    /// <summary>The item.</summary>
    public NativeMenuItem Item { get; }

    /// <summary>The window whose menu was used, when applicable.</summary>
    public string? WindowId { get; }
}

/// <summary>Shows native application, window and context menus.</summary>
public interface INativeMenuService : INativeCapability
{
    /// <summary>
    /// Sets the application menu (macOS menu bar) or the menu bar of a window (Windows, Linux).
    /// <see langword="null"/> removes it.
    /// </summary>
    ValueTask<NativeResult> SetApplicationMenuAsync(NativeMenu? menu, string? windowId = null, CancellationToken cancellationToken = default);

    /// <summary>Shows a context menu at a screen position (DIPs), or at the cursor when <paramref name="screenX"/>/<paramref name="screenY"/> are omitted.</summary>
    /// <returns>The chosen item id, or <see cref="NativeResultStatus.Cancelled"/> when dismissed.</returns>
    ValueTask<NativeResult<string>> ShowContextMenuAsync(NativeMenu menu, double? screenX = null, double? screenY = null, string? windowId = null, CancellationToken cancellationToken = default);

    /// <summary>Raised when any menu item managed by the service is invoked.</summary>
    event EventHandler<MenuItemInvokedEventArgs>? ItemInvoked;
}

/// <summary>Options for a tray icon.</summary>
public sealed record TrayIconOptions
{
    /// <summary>A stable id; generated when omitted.</summary>
    public string? Id { get; init; }

    /// <summary>The tooltip.</summary>
    public required string Tooltip { get; init; }

    /// <summary>The icon file (.ico on Windows, .png elsewhere). The app icon is used when neither path nor data is set.</summary>
    public string? IconPath { get; init; }

    /// <summary>The icon bytes, as an alternative to <see cref="IconPath"/>.</summary>
    public ReadOnlyMemory<byte>? IconData { get; init; }

    /// <summary>The context menu.</summary>
    public NativeMenu? Menu { get; init; }
}

/// <summary>A notification-area icon. Dispose to remove it.</summary>
public interface ITrayIcon : IAsyncDisposable
{
    /// <summary>The id.</summary>
    string Id { get; }

    /// <summary>Changes the tooltip.</summary>
    ValueTask<NativeResult> SetTooltipAsync(string tooltip, CancellationToken cancellationToken = default);

    /// <summary>Changes the icon.</summary>
    ValueTask<NativeResult> SetIconAsync(string? iconPath, ReadOnlyMemory<byte>? iconData = null, CancellationToken cancellationToken = default);

    /// <summary>Changes the menu.</summary>
    ValueTask<NativeResult> SetMenuAsync(NativeMenu? menu, CancellationToken cancellationToken = default);

    /// <summary>Raised when the icon is clicked with the primary button.</summary>
    event EventHandler? Clicked;

    /// <summary>Raised when the icon is double-clicked.</summary>
    event EventHandler? DoubleClicked;
}

/// <summary>Creates notification-area (system tray / menu bar extra) icons.</summary>
public interface ITrayService : INativeCapability
{
    /// <summary>Creates an icon.</summary>
    ValueTask<NativeResult<ITrayIcon>> CreateAsync(TrayIconOptions options, CancellationToken cancellationToken = default);

    /// <summary>The icons created by the app.</summary>
    IReadOnlyList<ITrayIcon> Icons { get; }
}

/// <summary>Shell file operations for desktop apps.</summary>
public interface IDesktopFileOperations : INativeCapability
{
    /// <summary>Opens the file manager with the item selected.</summary>
    ValueTask<NativeResult> RevealInFileManagerAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>Opens a file or folder with its default application.</summary>
    ValueTask<NativeResult> OpenPathAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>
    /// Opens a URL in the default browser or handler. Only <c>http</c>, <c>https</c> and <c>mailto</c> are allowed unless
    /// other schemes are explicitly permitted in the adapter options, to avoid launching arbitrary handlers from untrusted input.
    /// </summary>
    ValueTask<NativeResult> OpenUriAsync(Uri uri, CancellationToken cancellationToken = default);

    /// <summary>Moves a file or folder to the recycle bin / trash.</summary>
    ValueTask<NativeResult> MoveToTrashAsync(string path, CancellationToken cancellationToken = default);
}

/// <summary>Options for registering a custom URL scheme.</summary>
public sealed record ProtocolRegistrationOptions
{
    /// <summary>A user-facing name for the handler.</summary>
    public string? DisplayName { get; init; }

    /// <summary>The executable to launch; defaults to the current process.</summary>
    public string? ExecutablePath { get; init; }

    /// <summary>The argument template, where <c>%1</c> is replaced by the URL. Defaults to <c>"%1"</c>.</summary>
    public string? ArgumentsTemplate { get; init; }
}

/// <summary>Registers custom URL schemes at runtime (unpackaged Windows and Linux desktop apps).</summary>
/// <remarks>Packaged apps (MSIX, .app bundles, APK/IPA) declare schemes in their manifest; this service reports Unsupported there.</remarks>
public interface IProtocolRegistrationService : INativeCapability
{
    /// <summary>Registers <paramref name="scheme"/> for the current user.</summary>
    ValueTask<NativeResult> RegisterAsync(string scheme, ProtocolRegistrationOptions? options = null, CancellationToken cancellationToken = default);

    /// <summary>Removes a registration created by <see cref="RegisterAsync"/>.</summary>
    ValueTask<NativeResult> UnregisterAsync(string scheme, CancellationToken cancellationToken = default);

    /// <summary>Returns whether the scheme is registered to this application.</summary>
    ValueTask<NativeResult<bool>> IsRegisteredAsync(string scheme, CancellationToken cancellationToken = default);
}
