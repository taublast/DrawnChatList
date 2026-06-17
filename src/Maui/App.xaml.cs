using Microsoft.Extensions.DependencyInjection;

namespace DrawnChatList;

public partial class App : Application
{
	public App()
	{
		InitializeComponent();
	}

	protected override Window CreateWindow(IActivationState? activationState)
	{
		// Tiled-planes mobile test page. Swap back to new MainPage() for the original ItemsSource-churn repro.
		return new Window(new AppShell());
	}
}