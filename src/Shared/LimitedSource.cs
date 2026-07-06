using DrawnUi.Draw;

namespace DrawnChatList;

public class LimitedSource : WindowedSource<ChatMessage>
{
    public LimitedSource(int batch, int maxInMemory) : base(batch, maxInMemory,
        true)
    {
    }

    public void Remove(Guid id)
    {
        var found = Items.FirstOrDefault(x => x.Id == id);
        if (found != null)
        {
            // window-aware removal: keeps WindowStart/End and the total count consistent so later
            // LoadMore paging doesn't fetch shifted ranges (wrong messages / gaps after a delete)
            RemoveResident(found);
        }
    }
}