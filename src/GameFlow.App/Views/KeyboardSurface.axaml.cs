using System.Globalization;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using GameFlow.App.Converters;
using GameFlow.App.ViewModels;
using GameFlow.Infrastructure.Runtime.Input;

namespace GameFlow.App.Views;

public partial class KeyboardSurface : UserControl
{
    private static readonly VkPressedToBrushConverter KeyBrush = new();

    public KeyboardSurface()
    {
        InitializeComponent();
        BuildKeyboard();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void BuildKeyboard()
    {
        var canvas = this.FindControl<Canvas>("KeyCanvas")
            ?? throw new InvalidOperationException("Keyboard surface canvas was not loaded.");
        canvas.Width = KeyboardPhysicalLayout.SurfaceWidth;
        canvas.Height = KeyboardPhysicalLayout.SurfaceHeight;

        foreach (var key in KeyboardPhysicalLayout.Ansi104)
        {
            var keyCap = new Border
            {
                Width = key.Width,
                Height = key.Height,
                Child = CreateLegend(key),
            };
            keyCap.Classes.Add("keycap");
            keyCap.Bind(
                Border.BackgroundProperty,
                new Binding(nameof(DevicesViewModel.PressedKeysSet))
                {
                    Converter = KeyBrush,
                    ConverterParameter = key.VirtualKey.ToString("X", CultureInfo.InvariantCulture),
                });

            Canvas.SetLeft(keyCap, key.X);
            Canvas.SetTop(keyCap, key.Y);
            canvas.Children.Add(keyCap);
        }
    }

    private static Control CreateLegend(PhysicalKeyboardKey key)
    {
        var label = new TextBlock { Text = key.Label };
        if (key.Width <= KeyboardPhysicalLayout.KeyWidth && key.Label.Length >= 5)
        {
            // Pause and keypad Enter must fit a one-unit key without
            // clipping; ordinary legends keep the theme's larger size.
            label.FontSize = 8.25;
        }
        label.Classes.Add("keylabel");
        if (key.Hint is null)
        {
            return label;
        }

        var hint = new TextBlock { Text = key.Hint };
        hint.Classes.Add("keyhint");
        return new StackPanel
        {
            Spacing = 0,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { label, hint },
        };
    }
}
