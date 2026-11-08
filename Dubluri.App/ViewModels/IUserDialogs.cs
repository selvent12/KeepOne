namespace Dubluri.App.ViewModels;

/// <summary>Ferestrele de dialog, implementate de vedere.</summary>
public interface IUserDialogs
{
    Task<string?> PickFolderAsync(string title);
    Task<string?> PickSaveFileAsync(string title, string suggestedName);
    Task<bool> ConfirmAsync(string title, string message, string yesText, string noText, bool danger);
    Task ShowAsync(string title, string message);
}
