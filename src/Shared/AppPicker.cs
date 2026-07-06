using AppoMobi.Specials;
using DrawnUi.Draw;
using DrawnUi.Views;

namespace DrawnChatList;

public class AppPicker : SkiaLayer
{
    public AppPicker()
    {
        Title = "Select Option";

        IsVisible = false;
        ZIndex = 210;
        BlockGesturesBelow = true;
        BackgroundColor = Color.Parse("#99000000");
        HorizontalOptions = LayoutOptions.Fill;
        VerticalOptions = LayoutOptions.Fill;
        Children = new List<SkiaControl>()
        {
            new SkiaShape
            {
                Type = ShapeType.Rectangle,
                CornerRadius = 16,
                BackgroundColor = ChatTheme.BarBg,
                HorizontalOptions = LayoutOptions.Fill,
                VerticalOptions = LayoutOptions.End,
                Margin = new Thickness(8, 0, 8, 8),
                Padding = new Thickness(8, 12),
                Children =
                {
                    new SkiaStack
                    {
                        Spacing = 4,
                        HorizontalOptions = LayoutOptions.Fill,
                        Children =
                        {
                            new SkiaLabel
                            {
                                Text = "Dev Tools",
                                FontSize = 13,
                                TextColor = ChatTheme.IconMuted,
                                Margin = new Thickness(12, 4, 0, 8),
                            }.ObserveProperty(this, nameof(Title), me => //todo use compiled
                            {
                                me.Text = this.Title;
                            }),
                        }
                    }.Assign(out Sheet),
                }
            }.OnTapped(me => { /* swallow taps on the sheet */ }),
        };

        this.OnTapped(me => Hide(true));
    }

    public SkiaStack Sheet;

    public string Title
    {
        get;
        set
        {
            if (value == field) return;
            field = value;
            OnPropertyChanged();
        }
    }

    public void Setup(string title, IList<(string, Action)> options)
    {
        Title = title;

        Sheet.ClearChildren();
        foreach (var (label, action) in options)
            Sheet.AddSubView(BuildDevOptionRow(label, action));

        WasSetup = true;
    }

    private SkiaControl BuildDevOptionRow(string label, Action action)
    {
        return new SkiaShape
        {
            UseCache = SkiaCacheType.Operations,
            Type = ShapeType.Rectangle,
            CornerRadius = 10,
            BackgroundColor = ChatTheme.InputBg,
            HorizontalOptions = LayoutOptions.Fill,
            Padding = new Thickness(14, 12),
            Children =
            {
                new SkiaLabel
                {
                    Text = label,
                    FontSize = 15,
                    TextColor = Colors.White,
                    VerticalOptions = LayoutOptions.Center,
                },
            }
        }.OnTapped(me =>
        {
            Hide(true);
            action();
        });
    }


    public bool WasSetup { get; set; }

    public async void Show(bool animate)
    {
        if (animate)
        {
            Opacity = 0;
            IsVisible = true;
            await FadeToAsync(1, 160);
        }
        else
        {
            Opacity = 1;
            IsVisible = true;
        }
    }


    public async void Hide(bool animate)
    {
        if (animate)
        {
            await FadeToAsync(0, 140);
            IsVisible = false;
        }
        else
        {
            IsVisible = false;
        }
    }

}

 