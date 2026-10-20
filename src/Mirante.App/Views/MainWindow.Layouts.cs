using System.Windows;
using System.Windows.Controls;
using Mirante.App.ViewModels;
using Mirante.Core.Storage;
using Wpf.Ui.Controls;
using MenuItem = System.Windows.Controls.MenuItem;

namespace Mirante.App.Views;

/// <summary>
/// Layouts: named snapshots of the screen (windows, focus, cameras left out and, optionally, where
/// PTZ cameras point). Created from what is on screen; applied from the title bar, Ctrl+1–9 or the tray.
/// </summary>
internal sealed partial class MainWindow
{
    /// <summary>The layout applied last (checked in the menu); the screen may have changed since.</summary>
    private Guid? _lastLayout;

    private IReadOnlyList<LayoutPreset> Layouts => _settings.Load().Layouts;

    /// <summary>Puts the screen the way the layout says. Sound is never touched.</summary>
    internal void ApplyLayout(LayoutPreset layout)
    {
        if (!IsVisible)
        {
            ShowFromTray();
        }

        var tiles = _viewModel.Tiles.ToDictionary(t => t.Id);
        _viewModel.FocusedTile = null;
        _viewModel.SetExcluded(layout.ExcludedCameras);

        // Cameras in their own windows: close the ones the layout doesn't have, place the others.
        var wanted = layout.DetachedWindows
            .Where(d => tiles.TryGetValue(d.CameraId, out var t) && !t.IsExcluded)
            .ToDictionary(d => d.CameraId);
        foreach (var (tile, window) in _detached.ToList())
        {
            if (!wanted.ContainsKey(tile.Id))
            {
                window.Close();
            }
        }

        foreach (var state in wanted.Values)
        {
            var tile = tiles[state.CameraId];
            if (_detached.TryGetValue(tile, out var window))
            {
                window.Fullscreen.MoveTo(state.Placement);
            }
            else
            {
                _pendingPlacements[tile.Id] = state.Placement;
                _viewModel.ToggleDetach(tile);
            }
        }

        _fullscreen.MoveTo(layout.MainWindow);

        if (layout.FocusedCamera is { } focused && tiles.TryGetValue(focused, out var focus) && focus.IsVisibleInGrid)
        {
            _viewModel.FocusedTile = focus;
        }

        foreach (var position in layout.CameraPositions ?? [])
        {
            if (tiles.TryGetValue(position.CameraId, out var tile) && !tile.IsExcluded)
            {
                tile.Ptz.GoTo(new Core.Onvif.PtzPosition(position.Pan, position.Tilt, position.Zoom));
            }
        }

        _lastLayout = layout.Id;
        SaveLayout(); // reopen like this next time
        ShowToast($"Layout “{layout.Name}”");
    }

    private void ApplyLayoutByNumber(int number)
    {
        var layouts = Layouts;
        if (number >= 1 && number <= layouts.Count)
        {
            ApplyLayout(layouts[number - 1]);
        }
        else
        {
            ShowToast(layouts.Count == 0
                ? "Nenhum layout salvo ainda (Layouts › Salvar layout atual…)"
                : $"Não há layout {number}");
        }
    }

    /// <summary>Takes the screen as it is now.</summary>
    internal async Task<LayoutPreset> CaptureLayoutAsync(Guid id, string name, IReadOnlyCollection<Guid> excluded,
        bool includePositions)
    {
        List<LayoutCameraPosition>? positions = null;
        if (includePositions)
        {
            positions = [];
            foreach (var tile in _viewModel.Tiles.Where(t => t.Ptz.IsAvailable && !excluded.Contains(t.Id)))
            {
                if (await tile.Ptz.ReadPositionAsync() is { } p)
                {
                    positions.Add(new LayoutCameraPosition(tile.Id, Math.Clamp(p.Pan, -1, 1), Math.Clamp(p.Tilt, -1, 1),
                        p.Zoom is { } zoom ? Math.Clamp(zoom, -1, 1) : null));
                }
            }
        }

        return new LayoutPreset
        {
            Id = id,
            Name = name,
            MainWindow = _fullscreen.CapturePlacement(),
            DetachedWindows = [.. _detached
                .Where(d => !excluded.Contains(d.Key.Id))
                .Select(d => new DetachedWindowState(d.Key.Id, d.Value.Fullscreen.CapturePlacement()))],
            FocusedCamera = _viewModel.FocusedTile is { } f && !excluded.Contains(f.Id) ? f.Id : null,
            ExcludedCameras = [.. excluded],
            CameraPositions = positions,
        };
    }

    /// <summary>Adds the layout, or replaces the one with the same id or name.</summary>
    internal void StoreLayout(LayoutPreset layout)
    {
        var layouts = Layouts.ToList();
        var index = layouts.FindIndex(l => l.Id == layout.Id
            || l.Name.Equals(layout.Name, StringComparison.OrdinalIgnoreCase));
        if (index >= 0)
        {
            layouts[index] = layout with { Id = layouts[index].Id };
        }
        else
        {
            layouts.Add(layout);
        }

        _settings.Save(_settings.Load() with { Layouts = layouts });
    }

    private async Task SaveNewLayoutAsync()
    {
        var layouts = Layouts;
        if (layouts.Count >= LayoutPreset.MaxCount)
        {
            ShowToast($"Limite de {LayoutPreset.MaxCount} layouts; exclua um antes");
            return;
        }

        var dialog = new SaveLayoutWindow(
            _viewModel.Tiles.Select(t => (t.Id, t.Name, !t.IsExcluded)),
            $"Layout {layouts.Count + 1}",
            _viewModel.Tiles.Any(t => t.Ptz.IsAvailable)) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.Result is not { } choice)
        {
            return;
        }

        // What is left out takes effect now, so the screen matches what gets saved.
        _viewModel.SetExcluded(choice.Excluded);
        var existing = layouts.FirstOrDefault(l => l.Name.Equals(choice.Name, StringComparison.OrdinalIgnoreCase));
        var layout = await CaptureLayoutAsync(existing?.Id ?? Guid.NewGuid(), choice.Name, choice.Excluded.ToHashSet(),
            choice.IncludePositions);
        StoreLayout(layout);
        _lastLayout = layout.Id;
        SaveLayout();
        var number = Layouts.ToList().FindIndex(l => l.Id == layout.Id) + 1;
        ShowToast(existing is null
            ? $"Layout “{layout.Name}” salvo{(number <= 9 ? $" (Ctrl+{number})" : string.Empty)}"
            : $"Layout “{layout.Name}” atualizado");
    }

    /// <summary>Same layout, current screen; keeps its cameras and whether it moves them.</summary>
    private async Task UpdateLayoutAsync(LayoutPreset layout)
    {
        var excluded = _viewModel.Tiles.Where(t => t.IsExcluded).Select(t => t.Id).ToHashSet();
        StoreLayout(await CaptureLayoutAsync(layout.Id, layout.Name, excluded, layout.CameraPositions is not null));
        _lastLayout = layout.Id;
        ShowToast($"Layout “{layout.Name}” atualizado com a tela atual");
    }

    private void RenameLayout(LayoutPreset layout)
    {
        var dialog = new NameWindow("Renomear layout", $"Novo nome para “{layout.Name}”.", "Ex.: Noite", layout.Name, "Renomear")
        {
            Owner = this,
        };
        if (dialog.ShowDialog() != true || dialog.Result is not { } name || name == layout.Name)
        {
            return;
        }

        if (Layouts.Any(l => l.Id != layout.Id && l.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
        {
            ShowToast($"Já existe um layout “{name}”");
            return;
        }

        StoreLayout(layout with { Name = name });
    }

    private void DeleteLayout(LayoutPreset layout)
    {
        _settings.Save(_settings.Load() with { Layouts = [.. Layouts.Where(l => l.Id != layout.Id)] });
        if (_lastLayout == layout.Id)
        {
            _lastLayout = null;
        }

        ShowToast($"Layout “{layout.Name}” excluído");
    }

    private void ShowAllCameras()
    {
        _viewModel.SetExcluded([]);
        _lastLayout = null;
        SaveLayout();
    }

    // ----- Menus -----

    private void OnLayoutsClick(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu
        {
            PlacementTarget = LayoutsButton,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom,
        };
        FillLayoutMenu(menu.Items, full: true);
        menu.IsOpen = true;
    }

    private void OnHiddenCamerasClick(object sender, RoutedEventArgs e) => ShowAllCameras();

    /// <summary>Rebuilt every time it opens, so it always matches what is saved.</summary>
    private void OnTrayLayoutsOpened(object sender, RoutedEventArgs e)
    {
        TrayLayouts.Items.Clear();
        FillLayoutMenu(TrayLayouts.Items, full: false);
    }

    /// <summary>The layouts to apply and, in the title bar (<paramref name="full"/>), save/manage them.</summary>
    private void FillLayoutMenu(ItemCollection items, bool full)
    {
        static SymbolIcon Icon(SymbolRegular symbol) => new(symbol);
        var layouts = Layouts;
        for (var i = 0; i < layouts.Count; i++)
        {
            var layout = layouts[i];
            var item = new MenuItem
            {
                Header = layout.Name,
                InputGestureText = i < 9 ? $"Ctrl+{i + 1}" : string.Empty,
                IsChecked = layout.Id == _lastLayout,
            };
            item.Click += (_, _) => ApplyLayout(layout);
            items.Add(item);
        }

        if (layouts.Count == 0)
        {
            items.Add(new MenuItem { Header = "Nenhum layout salvo ainda", IsEnabled = false });
        }

        if (_viewModel.HasHiddenCameras)
        {
            var all = new MenuItem { Header = "Mostrar todas as câmeras", Icon = Icon(SymbolRegular.Eye24) };
            all.Click += (_, _) => ShowAllCameras();
            items.Add(all);
        }

        if (!full)
        {
            return;
        }

        items.Add(new Separator());
        var save = new MenuItem { Header = "Salvar layout atual…", Icon = Icon(SymbolRegular.Add24) };
        save.Click += async (_, _) => await SaveNewLayoutAsync();
        items.Add(save);

        if (layouts.FirstOrDefault(l => l.Id == _lastLayout) is { } last)
        {
            var update = new MenuItem { Header = $"Atualizar “{last.Name}” com a tela atual", Icon = Icon(SymbolRegular.ArrowSync24) };
            update.Click += async (_, _) => await UpdateLayoutAsync(last);
            items.Add(update);
        }

        if (layouts.Count == 0)
        {
            return;
        }

        var rename = new MenuItem { Header = "Renomear", Icon = Icon(SymbolRegular.Rename24) };
        var delete = new MenuItem { Header = "Excluir", Icon = Icon(SymbolRegular.Delete24) };
        foreach (var layout in layouts)
        {
            var r = new MenuItem { Header = layout.Name };
            r.Click += (_, _) => RenameLayout(layout);
            rename.Items.Add(r);
            var d = new MenuItem { Header = layout.Name };
            d.Click += (_, _) => DeleteLayout(layout);
            delete.Items.Add(d);
        }

        items.Add(rename);
        items.Add(delete);
    }
}
