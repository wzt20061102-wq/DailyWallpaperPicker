// Copyright (C) 2026 wzt20061102-wq
// Licensed under GNU GPL v3 only, with the additional Steamworks linking
// permission in LICENSE.STEAMWORKS-EXCEPTION. See LICENSE for full terms.

using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace DailyWallpaperPicker
{
    /// <summary>Shared visual tokens and control templates; no application state.</summary>
    internal static class PickerStyles
    {
        internal static readonly Brush CanvasBrush = Brush("#17181C");
        internal static readonly Brush CardBrush = Brush("#23252B");
        internal static readonly Brush BorderBrushToken = Brush("#31343C");
        internal static readonly Brush TextBrush = Brush("#F2F3F5");
        internal static readonly Brush MutedBrush = Brush("#A4A8B4");
        internal static readonly Brush AccentBrush = Brush("#C5CAFF");
        internal static readonly Brush AccentTextBrush = Brush("#20243A");
        internal static readonly Brush ErrorBrush = Brush("#FFB4AB");
        internal static Brush Brush(string color)
        {
            var value = (SolidColorBrush)new BrushConverter().ConvertFromString(color);
            value.Freeze();
            return value;
        }

        internal static Style CreateButtonStyle()
        {
            var style = new Style(typeof(Button));
            style.Setters.Add(new Setter(Control.ForegroundProperty, TextBrush));
            style.Setters.Add(new Setter(Control.BackgroundProperty, Brush("#2D3038")));
            style.Setters.Add(new Setter(Control.BorderBrushProperty, BorderBrushToken));
            style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(1)));
            style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(12, 7, 12, 7)));
            style.Setters.Add(new Setter(Control.MinHeightProperty, 40.0));
            style.Setters.Add(new Setter(Control.FontSizeProperty, 13.0));
            style.Setters.Add(new Setter(Control.FontFamilyProperty, new FontFamily("Microsoft YaHei UI")));
            style.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Center));
            style.Setters.Add(new Setter(Control.VerticalContentAlignmentProperty, VerticalAlignment.Center));
            style.Setters.Add(new Setter(Control.CursorProperty, Cursors.Hand));
            style.Setters.Add(new Setter(Control.FocusVisualStyleProperty, null));

            var template = new ControlTemplate(typeof(Button));
            var templateRoot = new FrameworkElementFactory(typeof(Grid));
            var border = new FrameworkElementFactory(typeof(Border), "ButtonBorder");
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(9));
            border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
            border.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty));
            border.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Control.BorderThicknessProperty));
            border.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));
            var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
            presenter.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            presenter.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            presenter.SetValue(ContentPresenter.RecognizesAccessKeyProperty, true);
            border.AppendChild(presenter);
            templateRoot.AppendChild(border);
            var feedback = new FrameworkElementFactory(typeof(Border), "ButtonFeedback");
            feedback.SetValue(Border.CornerRadiusProperty, new CornerRadius(9));
            feedback.SetValue(Border.BackgroundProperty, Brushes.White);
            feedback.SetValue(UIElement.OpacityProperty, 0.0);
            feedback.SetValue(UIElement.IsHitTestVisibleProperty, false);
            templateRoot.AppendChild(feedback);
            var focusRing = new FrameworkElementFactory(typeof(Border), "FocusRing");
            focusRing.SetValue(Border.CornerRadiusProperty, new CornerRadius(12));
            focusRing.SetValue(Border.BorderBrushProperty, Brush("#E6E8FF"));
            focusRing.SetValue(Border.BorderThicknessProperty, new Thickness(2));
            focusRing.SetValue(FrameworkElement.MarginProperty, new Thickness(-3));
            focusRing.SetValue(UIElement.VisibilityProperty, Visibility.Collapsed);
            focusRing.SetValue(UIElement.IsHitTestVisibleProperty, false);
            templateRoot.AppendChild(focusRing);
            template.VisualTree = templateRoot;
            var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            hover.Setters.Add(new Setter(UIElement.OpacityProperty, 0.07, "ButtonFeedback"));
            template.Triggers.Add(hover);
            var focus = new Trigger { Property = UIElement.IsKeyboardFocusedProperty, Value = true };
            focus.Setters.Add(new Setter(UIElement.VisibilityProperty, Visibility.Visible, "FocusRing"));
            template.Triggers.Add(focus);
            var down = new Trigger { Property = ButtonBase.IsPressedProperty, Value = true };
            down.Setters.Add(new Setter(Border.BackgroundProperty, Brushes.Black, "ButtonFeedback"));
            down.Setters.Add(new Setter(UIElement.OpacityProperty, 0.17, "ButtonFeedback"));
            template.Triggers.Add(down);
            var disabled = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
            disabled.Setters.Add(new Setter(UIElement.OpacityProperty, 0.42, "ButtonBorder"));
            template.Triggers.Add(disabled);
            style.Setters.Add(new Setter(Control.TemplateProperty, template));
            return style;
        }

        internal static Style CreateScrollBarStyle()
        {
            // A local template preserves the native Track/Thumb and page-scroll commands,
            // while removing the bright system arrows and trough from this dark window.
            const string xaml = @"
<Style xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'
       xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'
       TargetType='{x:Type ScrollBar}'>
  <Setter Property='Width' Value='10'/>
  <Setter Property='Background' Value='Transparent'/>
  <Setter Property='Focusable' Value='False'/>
  <Setter Property='Template'>
    <Setter.Value>
      <ControlTemplate TargetType='{x:Type ScrollBar}'>
        <Grid Background='Transparent'>
          <Track x:Name='PART_Track' IsDirectionReversed='True'
                 Orientation='{TemplateBinding Orientation}'
                 Minimum='{TemplateBinding Minimum}' Maximum='{TemplateBinding Maximum}'
                 Value='{TemplateBinding Value}' ViewportSize='{TemplateBinding ViewportSize}'>
            <Track.DecreaseRepeatButton>
              <RepeatButton Command='{x:Static ScrollBar.PageUpCommand}' Focusable='False' IsTabStop='False'>
                <RepeatButton.Template>
                  <ControlTemplate TargetType='{x:Type RepeatButton}'><Border Background='Transparent'/></ControlTemplate>
                </RepeatButton.Template>
              </RepeatButton>
            </Track.DecreaseRepeatButton>
            <Track.Thumb>
              <Thumb MinHeight='24' Background='#575C69' Focusable='False' IsTabStop='False'>
                <Thumb.Template>
                  <ControlTemplate TargetType='{x:Type Thumb}'>
                    <Border x:Name='Handle' Background='{TemplateBinding Background}' CornerRadius='4' Margin='2,0,2,0'/>
                    <ControlTemplate.Triggers>
                      <Trigger Property='IsMouseOver' Value='True'><Setter TargetName='Handle' Property='Background' Value='#818795'/></Trigger>
                      <Trigger Property='IsDragging' Value='True'><Setter TargetName='Handle' Property='Background' Value='#A4A8B4'/></Trigger>
                    </ControlTemplate.Triggers>
                  </ControlTemplate>
                </Thumb.Template>
              </Thumb>
            </Track.Thumb>
            <Track.IncreaseRepeatButton>
              <RepeatButton Command='{x:Static ScrollBar.PageDownCommand}' Focusable='False' IsTabStop='False'>
                <RepeatButton.Template>
                  <ControlTemplate TargetType='{x:Type RepeatButton}'><Border Background='Transparent'/></ControlTemplate>
                </RepeatButton.Template>
              </RepeatButton>
            </Track.IncreaseRepeatButton>
          </Track>
        </Grid>
      </ControlTemplate>
    </Setter.Value>
  </Setter>
</Style>";
            return (Style)System.Windows.Markup.XamlReader.Parse(xaml);
        }

        internal static Button MakeButton(string text, string name, bool primary)
        {
            var button = new Button { Content = text, Focusable = true };
            if (primary)
            {
                button.Background = AccentBrush;
                button.Foreground = AccentTextBrush;
                button.BorderBrush = AccentBrush;
                button.FontWeight = FontWeights.SemiBold;
            }
            AutomationProperties.SetName(button, name);
            return button;
        }

    }
}
