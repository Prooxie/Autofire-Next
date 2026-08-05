using GameFlow.Infrastructure.Theming;
using GameFlow.Infrastructure.Theming.Models;
using Xunit;

namespace GameFlow.Infrastructure.Tests.Runtime;

public sealed class ThemeJsonLoaderTests
{
    [Fact]
    public void Trailpad_is_loaded_as_conditional_moving_touch_marker()
    {
        const string json = """
        {
          "name": "Touch test",
          "width": 100,
          "height": 50,
          "children": [
            {
              "type": "trailpad",
              "image": "dot.png",
              "input": "1",
              "inputX": "12",
              "inputY": "-4",
              "width": 30,
              "height": 20
            }
          ]
        }
        """;

        var document = ThemeJsonLoader.LoadFromString(json);
        var trailPad = Assert.IsType<TrailPadNode>(Assert.Single(document.Children));
        var symbols = new ControllerStateSymbols();

        Assert.Equal("dot.png", trailPad.ImagePath);
        Assert.Equal(1, trailPad.Input.Evaluate(symbols));
        Assert.Equal(12, trailPad.InputX.Evaluate(symbols));
        Assert.Equal(-4, trailPad.InputY.Evaluate(symbols));
        Assert.Equal(30, trailPad.Width);
        Assert.Equal(20, trailPad.Height);
    }

    [Fact]
    public void Lightbar_is_loaded_as_runtime_tinted_image_mask()
    {
        const string json = """
        {
          "name": "Light test",
          "width": 100,
          "height": 50,
          "children": [
            {
              "type": "lightbar",
              "image": "light-mask.png",
              "x": 50,
              "y": 12,
              "width": 64,
              "height": 20,
              "center": true
            }
          ]
        }
        """;

        var document = ThemeJsonLoader.LoadFromString(json);
        var lightbar = Assert.IsType<LightbarNode>(Assert.Single(document.Children));

        Assert.Equal("light-mask.png", lightbar.ImagePath);
        Assert.Equal(50, lightbar.X);
        Assert.Equal(12, lightbar.Y);
        Assert.Equal(64, lightbar.Width);
        Assert.Equal(20, lightbar.Height);
        Assert.True(lightbar.Center);
    }
}
