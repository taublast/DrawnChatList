 using DrawnUi.Controls;
 using DrawnUi.Draw;
 using DrawnUi.Views;
 using System.Diagnostics;
 using System.Windows.Input;
 using AppoMobi.Specials;

 namespace DrawnChatList;

 
public partial class ChatPage  
{
    private const string SvgChevronDown =
        "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 24 24'><path d='M7.41 8.59 12 13.17l4.59-4.58L18 10l-6 6-6-6z'/></svg>";

    private const string SvgReply =
        "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 24 24'><path d='M10 9V5l-7 7 7 7v-4.1c5 0 8.5 1.6 11 5.1-1-5-4-10-11-11z'/></svg>";

    private const string SvgClose =
        "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 24 24'><path d='M19 6.41 17.59 5 12 10.59 6.41 5 5 6.41 10.59 12 5 17.59 6.41 19 12 13.41 17.59 19 19 17.59 13.41 12z'/></svg>";

    //inline image icon for the attach-photo button, no assets needed
    private const string SvgImage =
        "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 24 24'><path d='M21 19V5a2 2 0 0 0-2-2H5a2 2 0 0 0-2 2v14a2 2 0 0 0 2 2h14a2 2 0 0 0 2-2zM8.5 13.5l2.5 3 3.5-4.5 4.5 6H5z'/></svg>";

    private const string SvgSend =
        "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 24 24'><path d='M2.01 21 23 12 2.01 3 2 10l15 2-15 2z'/></svg>";

}