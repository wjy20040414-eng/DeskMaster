namespace DeskMaster;

internal sealed class DesktopApplication : ApplicationContext
{
    private readonly FolderStore store;
    private readonly Dictionary<Guid, FolderWindow> windows = [];
    private readonly int cardUnit;
    private DesktopFolder[] folders;

    public DesktopApplication(FolderStore store)
    {
        this.store = store;
        folders = store.Load();
        var grid = DesktopGrid.Read();
        cardUnit = grid.CardUnit(FolderWindow.DesktopIconSize);
        var restored = new List<DesktopFolder>();
        foreach (var saved in folders)
        {
            var position = FindSlot(grid, saved, restored);
            var folder = position is Point slot ? saved with { Location = slot } : saved;
            restored.Add(folder);
            Open(folder);
        }
        if (!folders.SequenceEqual(restored))
            Commit(restored.ToArray());
    }

    internal void ShowFolders()
    {
        foreach (var window in windows.Values)
            window.ShowOnDesktop();
    }

    internal void CreateFolder()
    {
        Rectangle area = Screen.FromPoint(Cursor.Position).WorkingArea;
        var folder = new DesktopFolder(Guid.NewGuid(), "新建文件夹", area.Location, 1, []);
        if (FindSlot(DesktopGrid.Read(), folder, folders) is not Point slot)
        {
            MessageBox.Show("当前屏幕没有可用的桌面格子。", "DeskMaster");
            return;
        }
        folder = folder with { Location = slot };
        if (Commit([.. folders, folder]))
            Open(folder);
    }

    private void Open(DesktopFolder folder)
    {
        var window = new FolderWindow(folder,
            updated => Commit(folders.Select(item => item.Id == updated.Id ? updated : item).ToArray()),
            () => Delete(folder.Id),
            updated => FindSlot(DesktopGrid.Read(), updated, folders), cardUnit);
        windows.Add(folder.Id, window);
        window.ShowOnDesktop();
    }

    private Point? FindSlot(DesktopGrid grid, DesktopFolder folder, IEnumerable<DesktopFolder> others)
    {
        int iconSize = FolderWindow.DesktopIconSize;
        var occupied = others.Where(item => item.Id != folder.Id).Select(item =>
        {
            int side = FolderWindow.CollapsedSide(cardUnit, item.IconColumns);
            return new Rectangle(item.Location, new Size(side, side + FolderWindow.CaptionHeight));
        });
        int folderSide = FolderWindow.CollapsedSide(cardUnit, folder.IconColumns);
        var footprint = new Size(folderSide, folderSide + FolderWindow.CaptionHeight);
        return grid.FindSlot(folder.Location, footprint, iconSize, folder.IconColumns, occupied);
    }

    private void Delete(Guid id)
    {
        if (!Commit(folders.Where(folder => folder.Id != id).ToArray()))
            return;

        windows.Remove(id, out var window);
        window?.Dispose();
    }

    private bool Commit(DesktopFolder[] updated)
    {
        try
        {
            store.Save(updated);
            folders = updated;
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show($"无法保存修改。\n\n{error.Message}", "DeskMaster",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            foreach (var window in windows.Values)
                window.Dispose();
        }
        base.Dispose(disposing);
    }
}
