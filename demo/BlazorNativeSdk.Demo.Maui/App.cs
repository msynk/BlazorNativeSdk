namespace BlazorNativeSdk.Demo.Maui;

/// <summary>The MAUI application.</summary>
public sealed class App : Application
{
    /// <inheritdoc />
    protected override Window CreateWindow(IActivationState? activationState) =>
        new(new MainPage()) { Title = "BlazorNativeSdk Demo" };
}
