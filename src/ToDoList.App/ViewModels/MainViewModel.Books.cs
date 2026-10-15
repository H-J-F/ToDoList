using ToDoList.Core;
using ToDoList.Storage;

namespace ToDoList.App.ViewModels;

public sealed partial class MainViewModel
{
    private void RefreshVisibleBooks()
    {
        Reconcile(VisibleBooks, Books.Where(b => _showHiddenBooks || !b.IsHidden).ToArray(), b => b.BookId);
        Raise(nameof(HasHiddenBooks)); Raise(nameof(WelcomeTitle)); Raise(nameof(WelcomeDescription));
    }
    private void SetShowHiddenBooks(bool value)
    {
        _showHiddenBooks = value; RefreshVisibleBooks(); Raise(nameof(ShowHiddenBooks));
    }
    private void ClearCurrentBook()
    {
        var previous = Settings.LastBook;
        Settings.LastBook = null;
        try { SaveSettings(); } catch { Settings.LastBook = previous; throw; }
        CancelQuery(); IsLoading = false; Repository = null; CurrentBook = null; CurrentProjectId = null; CurrentQuery = null;
        Tasks.Clear(); Projects.Clear(); DraftProjects.Clear(); HasOlder = HasNewer = false; NotifyList();
    }
    public async Task ShowHiddenBooksAsync(bool value)
    {
        if (!value && CurrentBook?.IsHidden == true)
        {
            var next = Books.FirstOrDefault(b => !b.IsHidden);
            if (next != null) await OpenBookAsync(next);
            else ClearCurrentBook();
        }
        SetShowHiddenBooks(value);
    }
    public async Task SetCurrentBookHiddenAsync(bool hidden)
    {
        if (CurrentBook is not { } book || book.IsHidden == hidden) return;
        // Prepare navigation first. A corrupt next book or failed settings write
        // must not persist a hide action that the UI could not complete.
        var next = hidden && !_showHiddenBooks ? Books.FirstOrDefault(b => b.BookId != book.BookId && !b.IsHidden) : null;
        if (next != null) await OpenBookAsync(next);
        try
        {
            await new TaskRepository(book.Path).SetSettingAsync(BookInfo.HiddenSetting, hidden ? "true" : "false");
        }
        catch
        {
            if (next != null) await OpenBookAsync(book);
            throw;
        }
        await RefreshBooksAsync();
        if (CurrentBook?.BookId == book.BookId) CurrentBook = Books.First(b => b.BookId == book.BookId);
        if (hidden && !_showHiddenBooks && next == null)
        {
            try { ClearCurrentBook(); }
            catch { SetShowHiddenBooks(true); throw; }
        }
        Message = hidden ? "待办笔记已隐藏，可从笔记菜单显示隐藏笔记。" : "待办笔记已取消隐藏。";
    }
}
