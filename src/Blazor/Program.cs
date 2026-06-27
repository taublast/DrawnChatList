global using Color = DrawnUi.Color;

using DrawnChatList.Blazor;
using DrawnUi.Draw;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

builder.Services.AddScoped(sp => new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) });

await Super.UseDrawnUi(builder)
    .WithBaseUrl(builder.HostEnvironment.BaseAddress)
    .WithOptions(options => options.UseDesktopKeyboard = true)
    .ConfigureStyles(styles =>
    {
        styles.AddStyle(new Style()
        {
            ApplyToDerivedTypes = true,
            TargetType = typeof(SkiaLabel),
            Setters =
            {
                new Setter()
                {
                    Property = SkiaLabel.FontFamilyProperty,
                    Value = "OpenSansRegular"
                }
            }
        });
    })
    .ConfigureFonts(fonts =>
    {
        fonts.AddFont("fonts/OpenSans-Regular.ttf", "OpenSansRegular");
        fonts.AddFont("fonts/OpenSans-Semibold.ttf", "OpenSansSemibold");
    })
    .BuildAndRunAsync();