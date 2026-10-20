using BlazorNativeSdk.Desktop;
using BlazorNativeSdk.WindowsPlatform.Interop;
using Microsoft.Extensions.Logging.Abstractions;

namespace BlazorNativeSdk.WindowsPlatform.Tests;

public sealed class ScreenServiceTests
{
    [Fact]
    public async Task Enumerates_at_least_one_primary_display_with_positive_dip_sizes()
    {
        var service = new WindowsScreenService();
        var result = await service.GetDisplaysAsync(TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess, result.Error?.ToString());
        Assert.NotEmpty(result.Value);
        Assert.Single(result.Value, d => d.IsPrimary);
        Assert.True(result.Value[0].IsPrimary, "The primary display is listed first.");
        foreach (var display in result.Value)
        {
            Assert.StartsWith(@"\\.\", display.Id, StringComparison.Ordinal);
            Assert.True(display.Bounds.Width > 0 && display.Bounds.Height > 0);
            Assert.True(display.WorkArea.Width > 0 && display.WorkArea.Height > 0);
            Assert.True(display.ScaleFactor >= 1.0);
            Assert.True(display.WorkArea.Width <= display.Bounds.Width + 0.5);
            Assert.True(display.WorkArea.Height <= display.Bounds.Height + 0.5);
        }
    }
}

public sealed class VirtualKeyMapTests
{
    [Fact]
    public void Every_key_maps_to_a_virtual_key()
    {
        foreach (var key in Enum.GetValues<NativeKey>().Where(k => k != NativeKey.None))
        {
            Assert.True(VirtualKeyMap.TryGetVirtualKey(key, out var vk), $"{key} has no virtual key.");
            Assert.InRange(vk, 0x01u, 0xFEu);
        }

        Assert.False(VirtualKeyMap.TryGetVirtualKey(NativeKey.None, out _));
    }

    [Theory]
    [InlineData(NativeKey.A, 0x41u)]
    [InlineData(NativeKey.Z, 0x5Au)]
    [InlineData(NativeKey.D0, 0x30u)]
    [InlineData(NativeKey.D9, 0x39u)]
    [InlineData(NativeKey.F1, 0x70u)]
    [InlineData(NativeKey.F23, 0x86u)]
    [InlineData(NativeKey.F24, 0x87u)]
    [InlineData(NativeKey.NumPad5, 0x65u)]
    [InlineData(NativeKey.Space, 0x20u)]
    [InlineData(NativeKey.Enter, 0x0Du)]
    [InlineData(NativeKey.Escape, 0x1Bu)]
    [InlineData(NativeKey.Left, 0x25u)]
    [InlineData(NativeKey.Down, 0x28u)]
    [InlineData(NativeKey.Comma, 0xBCu)]
    [InlineData(NativeKey.Slash, 0xBFu)]
    [InlineData(NativeKey.BracketLeft, 0xDBu)]
    [InlineData(NativeKey.PrintScreen, 0x2Cu)]
    [InlineData(NativeKey.MediaPlayPause, 0xB3u)]
    [InlineData(NativeKey.VolumeMute, 0xADu)]
    public void Maps_known_keys(NativeKey key, uint expected)
    {
        Assert.True(VirtualKeyMap.TryGetVirtualKey(key, out var vk));
        Assert.Equal(expected, vk);
    }

    [Fact]
    public void Maps_modifiers_and_resolves_command_or_control_to_control()
    {
        Assert.Equal(Win32.MOD_CONTROL | Win32.MOD_ALT | Win32.MOD_SHIFT | Win32.MOD_WIN,
            VirtualKeyMap.ToHotKeyModifiers(KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Shift | KeyModifiers.Meta));

        var resolved = KeyGesture.Parse("CmdOrCtrl+Shift+K").Resolve(NativePlatform.Windows);
        Assert.Equal(KeyModifiers.Control | KeyModifiers.Shift, resolved.Modifiers);
        Assert.Equal(Win32.MOD_CONTROL | Win32.MOD_SHIFT, VirtualKeyMap.ToHotKeyModifiers(resolved.Modifiers));
        Assert.Equal(Win32.MOD_CONTROL, VirtualKeyMap.ToHotKeyModifiers(KeyModifiers.CommandOrControl));
    }

    [Theory]
    [InlineData("CommandOrControl+Shift+K", "Ctrl+Shift+K")]
    [InlineData("Alt+F4", "Alt+F4")]
    [InlineData("Ctrl+0", "Ctrl+0")]
    [InlineData("Ctrl+Plus", "Ctrl++")]
    [InlineData("Super+PageDown", "Win+PgDn")]
    public void Formats_accelerator_text(string gesture, string expected) =>
        Assert.Equal(expected, VirtualKeyMap.ToDisplayString(KeyGesture.Parse(gesture)));
}

public sealed class MenuPlannerTests
{
    [Fact]
    public void Assigns_unique_command_ids_to_invokable_items_including_nested_ones()
    {
        var menu = NativeMenu.Create(
            NativeMenuItem.Submenu("&File",
                NativeMenuItem.Create("open", "&Open", accelerator: "CmdOrCtrl+O"),
                NativeMenuItem.Separator(),
                NativeMenuItem.Submenu("Recent", NativeMenuItem.Create("recent-1", "One"), NativeMenuItem.Create("recent-2", "Two")),
                NativeMenuItem.ForRole(NativeMenuRole.Quit)),
            NativeMenuItem.Create("help", "Help"));

        var plan = Win32MenuPlanner.Plan(menu, firstCommandId: 100);

        Assert.Equal(5, plan.Commands.Count);
        Assert.Equal(["open", "recent-1", "recent-2", "role.quit", "help"], plan.Commands.OrderBy(c => c.Key).Select(c => c.Value.Id));
        Assert.Equal(Enumerable.Range(100, 5).Select(i => (uint)i), plan.Commands.Keys.Order());
        Assert.True(plan.TryGetItem(101, out var recent) && recent.Id == "recent-1");
        Assert.False(plan.TryGetItem(0, out _));

        var file = plan.Entries[0];
        Assert.True(file.IsSubmenu);
        Assert.Equal(0u, file.CommandId);
        Assert.Equal("&Open\tCtrl+O", file.Children[0].Text);
        Assert.True(file.Children[1].IsSeparator);
        Assert.Equal(0u, file.Children[1].CommandId);
        Assert.Equal("E&xit", file.Children[3].Text);
    }

    [Fact]
    public void Maps_checked_radio_and_disabled_states()
    {
        var menu = NativeMenu.Create(
            new NativeMenuItem { Id = "check", Label = "Check", Kind = NativeMenuItemKind.Checkbox, IsChecked = true },
            new NativeMenuItem { Id = "radio", Label = "Radio", Kind = NativeMenuItemKind.Radio, IsChecked = true, RadioGroup = "g" },
            new NativeMenuItem { Id = "off", Label = "Off", IsEnabled = false },
            new NativeMenuItem { Id = "normal-checked", Label = "Normal", IsChecked = true });

        var entries = Win32MenuPlanner.Plan(menu).Entries;
        Assert.Equal(Win32.MFT_STRING, entries[0].Type);
        Assert.Equal(Win32.MFS_CHECKED, entries[0].State);
        Assert.Equal(Win32.MFT_STRING | Win32.MFT_RADIOCHECK, entries[1].Type);
        Assert.Equal(Win32.MFS_CHECKED, entries[1].State);
        Assert.Equal(Win32.MFS_GRAYED, entries[2].State);
        Assert.Equal(0u, entries[3].State);
    }

    [Fact]
    public void Rejects_zero_as_first_command_id() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => Win32MenuPlanner.Plan(NativeMenu.Create(), firstCommandId: 0));

    [Fact]
    public void Creates_native_menus_matching_the_plan()
    {
        var menu = NativeMenu.Create(
            NativeMenuItem.Create("a", "A"),
            NativeMenuItem.Separator(),
            NativeMenuItem.Submenu("Sub", NativeMenuItem.Create("b", "B"), NativeMenuItem.Create("c", "C")));
        var plan = Win32MenuPlanner.Plan(menu, Win32MenuPlanner.MenuBarFirstId);

        using var popup = Win32MenuFactory.Create(plan, menuBar: false);
        Assert.False(popup.IsInvalid);
        Assert.Equal(3, NativeMethods.GetMenuItemCount(popup.DangerousGetHandle()));

        using var bar = Win32MenuFactory.Create(plan, menuBar: true);
        Assert.Equal(3, NativeMethods.GetMenuItemCount(bar.DangerousGetHandle()));
    }
}

public sealed class GlobalShortcutTests
{
    private static readonly KeyGesture s_rareGesture = KeyGesture.Parse("Ctrl+Alt+Shift+F23");

    [Fact]
    public async Task Registers_and_unregisters_a_hot_key()
    {
        await using var service = new WindowsGlobalShortcutService(new MessageWindowThread(), NullLogger<WindowsGlobalShortcutService>.Instance, ownsThread: true);
        Assert.True((await service.GetStatusAsync(TestContext.Current.CancellationToken)).IsAvailable);

        var first = await service.RegisterAsync(s_rareGesture, () => ValueTask.CompletedTask, TestContext.Current.CancellationToken);
        Assert.True(first.IsSuccess, first.Error?.ToString());
        Assert.Equal(s_rareGesture, first.Value.Gesture);
        Assert.Equal([s_rareGesture], service.RegisteredGestures);

        await first.Value.DisposeAsync();
        await first.Value.DisposeAsync(); // idempotent
        Assert.Empty(service.RegisteredGestures);

        // Unregistering released the hot key, so it can be registered again.
        var second = await service.RegisterAsync(s_rareGesture, () => ValueTask.CompletedTask, TestContext.Current.CancellationToken);
        Assert.True(second.IsSuccess, second.Error?.ToString());
        await second.Value.DisposeAsync();
    }

    [Fact]
    public async Task Reports_a_conflict_when_the_gesture_is_already_registered()
    {
        await using var owner = new WindowsGlobalShortcutService(new MessageWindowThread(), NullLogger<WindowsGlobalShortcutService>.Instance, ownsThread: true);
        await using var other = new WindowsGlobalShortcutService(new MessageWindowThread(), NullLogger<WindowsGlobalShortcutService>.Instance, ownsThread: true);
        var gesture = KeyGesture.Parse("Ctrl+Alt+Shift+F22");

        var registered = await owner.RegisterAsync(gesture, () => ValueTask.CompletedTask, TestContext.Current.CancellationToken);
        Assert.True(registered.IsSuccess, registered.Error?.ToString());

        var conflict = await other.RegisterAsync(gesture, () => ValueTask.CompletedTask, TestContext.Current.CancellationToken);
        Assert.False(conflict.IsSuccess);
        Assert.Equal(NativeResultStatus.Failed, conflict.Status);
        Assert.Equal(NativeErrorCodes.Conflict, conflict.Error!.Code);
        Assert.Empty(other.RegisteredGestures);

        // Disposing the owning service releases its hot keys.
        owner.Dispose();
        var afterRelease = await other.RegisterAsync(gesture, () => ValueTask.CompletedTask, TestContext.Current.CancellationToken);
        Assert.True(afterRelease.IsSuccess, afterRelease.Error?.ToString());
    }

    [Fact]
    public async Task Rejects_keys_without_a_virtual_key()
    {
        await using var service = new WindowsGlobalShortcutService(new MessageWindowThread(), NullLogger<WindowsGlobalShortcutService>.Instance, ownsThread: true);
        var result = await service.RegisterAsync(new KeyGesture(KeyModifiers.Control, NativeKey.None), () => ValueTask.CompletedTask, TestContext.Current.CancellationToken);
        Assert.Equal(NativeErrorCodes.InvalidArgument, result.Error!.Code);
    }
}

public sealed class MessageWindowThreadTests
{
    [Fact]
    public async Task Runs_work_on_a_dedicated_sta_thread_and_dispatches_messages_to_handlers()
    {
        using var thread = new MessageWindowThread();
        var (threadId, apartment) = await thread.InvokeAsync(() => (Environment.CurrentManagedThreadId, Thread.CurrentThread.GetApartmentState()));
        Assert.NotEqual(Environment.CurrentManagedThreadId, threadId);
        Assert.Equal(ApartmentState.STA, apartment);
        Assert.NotEqual(0, thread.MessageWindow);
        Assert.NotEqual(0, thread.ShellWindow);
        Assert.NotEqual(thread.MessageWindow, thread.ShellWindow);

        const uint TestMessage = Win32.WM_APP + 0x123;
        var received = new TaskCompletionSource<nint>(TaskCreationOptions.RunContinuationsAsynchronously);
        using (thread.AddHandler((nint hwnd, uint message, nint wParam, nint lParam, out nint result) =>
        {
            result = 0;
            if (message != TestMessage)
            {
                return false;
            }

            received.TrySetResult(wParam);
            return true;
        }))
        {
            Assert.True(NativeMethods.PostMessageW(thread.MessageWindow, TestMessage, 42, 0));
            Assert.Equal(42, await received.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        }

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => thread.InvokeAsync<int>(() => throw new InvalidOperationException("boom")));
        Assert.Equal("boom", failure.Message);
    }

    [Fact]
    public async Task Dispose_stops_the_loop_and_destroys_the_windows()
    {
        var thread = new MessageWindowThread();
        var window = thread.MessageWindow;
        Assert.True(NativeMethods.IsWindow(window));

        thread.Dispose();
        thread.Dispose(); // idempotent
        Assert.False(NativeMethods.IsWindow(window));
        Assert.False(thread.IsRunning);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => thread.InvokeAsync(() => 1));
    }
}
