namespace DrawnChatList;

/// <summary>
/// Contract for cells to talk to the page (mirrors the original app's view-model commands,
/// reached from the cell via Parent.BindingContext).
/// </summary>
public interface IChatCellActions
{
    void ShowImageFullscreen(ChatMessage msg);

    /// <summary>Long-press a bubble: quote it in the typing bar (reply panel).</summary>
    void ReplyToMessage(ChatMessage msg);

    /// <summary>Tap a quote: jump to the quoted message, even outside the resident window.</summary>
    void ScrollToMessage(ChatMessage msg);

    void ShowMessageOptions(ChatMessage msg);
}