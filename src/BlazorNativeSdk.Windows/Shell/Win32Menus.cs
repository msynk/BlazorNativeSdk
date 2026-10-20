using System.ComponentModel;
using System.Runtime.InteropServices;
using BlazorNativeSdk.Desktop;
using BlazorNativeSdk.WindowsPlatform.Interop;

namespace BlazorNativeSdk.WindowsPlatform;

/// <summary>One planned Win32 menu entry.</summary>
/// <param name="Item">The source item.</param>
/// <param name="CommandId">The numeric command id (0 for separators and submenus).</param>
/// <param name="Type">The <c>MFT_*</c> type flags.</param>
/// <param name="State">The <c>MFS_*</c> state flags.</param>
/// <param name="Text">The display text including the access key marker and the tab-separated accelerator text.</param>
/// <param name="Children">Submenu entries.</param>
internal sealed record MenuEntryPlan(NativeMenuItem Item, uint CommandId, uint Type, uint State, string? Text, IReadOnlyList<MenuEntryPlan> Children)
{
    public bool IsSeparator => (Type & Win32.MFT_SEPARATOR) != 0;

    public bool IsSubmenu => !IsSeparator && (Children.Count > 0 || Item.Kind == NativeMenuItemKind.Submenu);
}

/// <summary>A <see cref="NativeMenu"/> translated to Win32 menu entries with numeric command ids.</summary>
internal sealed class MenuPlan
{
    private readonly Dictionary<uint, NativeMenuItem> _commands;

    public MenuPlan(IReadOnlyList<MenuEntryPlan> entries, Dictionary<uint, NativeMenuItem> commands)
    {
        Entries = entries;
        _commands = commands;
    }

    public IReadOnlyList<MenuEntryPlan> Entries { get; }

    public IReadOnlyDictionary<uint, NativeMenuItem> Commands => _commands;

    public bool TryGetItem(uint commandId, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out NativeMenuItem? item) =>
        _commands.TryGetValue(commandId, out item);
}

/// <summary>Translates <see cref="NativeMenu"/> to Win32 menus: command id allocation, flags and labels (pure, unit-tested).</summary>
internal static class Win32MenuPlanner
{
    /// <summary>The first command id used for window menu bars (kept well above ids that hosts typically use).</summary>
    public const uint MenuBarFirstId = 0xA000;

    /// <summary>The largest command id <c>WM_COMMAND</c> can carry.</summary>
    public const uint MaxCommandId = 0xFFFF;

    public static MenuPlan Plan(NativeMenu menu, uint firstCommandId = 1, uint maxCommandId = MaxCommandId)
    {
        ArgumentNullException.ThrowIfNull(menu);
        if (firstCommandId == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(firstCommandId), "Command ids start at 1; 0 means 'no selection'.");
        }

        var commands = new Dictionary<uint, NativeMenuItem>();
        var next = firstCommandId;
        var entries = PlanItems(menu.Items, commands, ref next, maxCommandId, depth: 0);
        return new MenuPlan(entries, commands);
    }

    /// <summary>Builds the label: the item label (or a default for its role), then <c>\t</c> and the accelerator text.</summary>
    public static string FormatLabel(NativeMenuItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        var label = !string.IsNullOrEmpty(item.Label) ? item.Label : DefaultLabel(item.Role) ?? item.Id;
        return item.Accelerator is { } accelerator ? label + "\t" + VirtualKeyMap.ToDisplayString(accelerator) : label;
    }

    private static List<MenuEntryPlan> PlanItems(IReadOnlyList<NativeMenuItem> items, Dictionary<uint, NativeMenuItem> commands, ref uint next, uint max, int depth)
    {
        const int MaxDepth = 32;
        if (depth > MaxDepth)
        {
            throw new ArgumentException($"Menus cannot be nested deeper than {MaxDepth} levels.");
        }

        var entries = new List<MenuEntryPlan>(items.Count);
        foreach (var item in items)
        {
            ArgumentNullException.ThrowIfNull(item);
            if (item.Kind == NativeMenuItemKind.Separator)
            {
                entries.Add(new MenuEntryPlan(item, 0, Win32.MFT_SEPARATOR, 0, null, []));
                continue;
            }

            var state = item.IsEnabled ? 0u : Win32.MFS_GRAYED;
            if (item.Kind == NativeMenuItemKind.Submenu || item.Items.Count > 0)
            {
                var children = PlanItems(item.Items, commands, ref next, max, depth + 1);
                entries.Add(new MenuEntryPlan(item, 0, Win32.MFT_STRING, state, FormatLabel(item), children));
                continue;
            }

            if (next > max)
            {
                throw new ArgumentException($"The menu has more items than available command ids ({commands.Count} allocated, maximum id {max}).");
            }

            var type = Win32.MFT_STRING;
            if (item.Kind == NativeMenuItemKind.Radio)
            {
                type |= Win32.MFT_RADIOCHECK;
            }

            if (item.IsChecked && item.Kind is NativeMenuItemKind.Checkbox or NativeMenuItemKind.Radio)
            {
                state |= Win32.MFS_CHECKED;
            }

            var id = next++;
            commands[id] = item;
            entries.Add(new MenuEntryPlan(item, id, type, state, FormatLabel(item), []));
        }

        return entries;
    }

    private static string? DefaultLabel(NativeMenuRole role) => role switch
    {
        NativeMenuRole.About => "&About",
        NativeMenuRole.Preferences => "&Settings",
        NativeMenuRole.Quit => "E&xit",
        NativeMenuRole.Hide => "&Hide",
        NativeMenuRole.HideOthers => "Hide &Others",
        NativeMenuRole.ShowAll => "Show &All",
        NativeMenuRole.Undo => "&Undo",
        NativeMenuRole.Redo => "&Redo",
        NativeMenuRole.Cut => "Cu&t",
        NativeMenuRole.Copy => "&Copy",
        NativeMenuRole.Paste => "&Paste",
        NativeMenuRole.SelectAll => "Select &All",
        NativeMenuRole.Minimize => "Mi&nimize",
        NativeMenuRole.Zoom => "Ma&ximize",
        NativeMenuRole.Close => "&Close",
        NativeMenuRole.ToggleFullScreen => "&Full Screen",
        NativeMenuRole.WindowMenu => "&Window",
        NativeMenuRole.HelpMenu => "&Help",
        _ => null,
    };
}

/// <summary>Creates HMENUs from a <see cref="MenuPlan"/>.</summary>
internal static unsafe class Win32MenuFactory
{
    /// <summary>Creates a popup menu (context/tray menus) or a menu bar (window menus).</summary>
    public static SafeMenuHandle Create(MenuPlan plan, bool menuBar)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var root = new SafeMenuHandle(menuBar ? NativeMethods.CreateMenu() : NativeMethods.CreatePopupMenu());
        if (root.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Could not create a menu.");
        }

        try
        {
            Populate(root.DangerousGetHandle(), plan.Entries);
            return root;
        }
        catch
        {
            root.Dispose();
            throw;
        }
    }

    private static void Populate(nint menu, IReadOnlyList<MenuEntryPlan> entries)
    {
        for (var index = 0; index < entries.Count; index++)
        {
            var entry = entries[index];
            nint submenu = 0;
            if (entry.IsSubmenu)
            {
                submenu = NativeMethods.CreatePopupMenu();
                if (submenu == 0)
                {
                    throw new Win32Exception(Marshal.GetLastPInvokeError(), "Could not create a submenu.");
                }

                try
                {
                    Populate(submenu, entry.Children);
                }
                catch
                {
                    _ = NativeMethods.DestroyMenu(submenu);
                    throw;
                }
            }

            fixed (char* text = entry.Text ?? string.Empty)
            {
                var info = new MENUITEMINFOW
                {
                    cbSize = (uint)sizeof(MENUITEMINFOW),
                    fMask = Win32.MIIM_FTYPE | Win32.MIIM_STATE | Win32.MIIM_ID,
                    fType = entry.Type,
                    fState = entry.State,
                    wID = entry.CommandId,
                };

                if (!entry.IsSeparator)
                {
                    info.fMask |= Win32.MIIM_STRING;
                    info.dwTypeData = text;
                    info.cch = (uint)(entry.Text?.Length ?? 0);
                }

                if (submenu != 0)
                {
                    info.fMask |= Win32.MIIM_SUBMENU;
                    info.hSubMenu = submenu;
                }

                if (!NativeMethods.InsertMenuItemW(menu, (uint)index, true, &info))
                {
                    var error = Marshal.GetLastPInvokeError();
                    if (submenu != 0)
                    {
                        _ = NativeMethods.DestroyMenu(submenu);
                    }

                    throw new Win32Exception(error, "Could not insert a menu item.");
                }
            }
        }
    }
}
