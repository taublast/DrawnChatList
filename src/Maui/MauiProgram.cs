using Microsoft.Extensions.Logging;
using DrawnUi.Draw;

namespace DrawnChatList;

public static class MauiProgram
{
	public static MauiApp CreateMauiApp()
	{
		var builder = MauiApp.CreateBuilder();
		builder
			.UseMauiApp<App>()
            .UseDrawnUi(new()
            {
                UseDesktopKeyboard = true, //will not work with maui shell on apple!!
                DesktopWindow = new()
                {
                    Height = 700,
                    Width = 400,
                }
            })
            .ConfigureFonts(fonts =>
			{
				fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
				fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
			});

#if DEBUG
		builder.Logging.AddDebug();
#endif

		return builder.Build();
	}
}
