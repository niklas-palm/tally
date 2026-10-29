using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Tally;

/// <summary>
/// What the drawer says when a call starts: offer to record it, say it's being recorded (auto mode), or
/// ask whether a new call is the meeting just stopped. Tally's own icon makes it plainly Tally's; the model decides
/// when it shows and when it folds.
/// </summary>
internal sealed class DrawerPanel : Grid
{
    private readonly AppModel model;

    public DrawerPanel(AppModel model)
    {
        this.model = model;
        Padding = new Thickness(14);
    }

    public void Draw(CallPrompt prompt)
    {
        var icon = new Image { Width = 32, Height = 32, VerticalAlignment = VerticalAlignment.Top, Source = new BitmapImage(new Uri(Path.Combine(AppContext.BaseDirectory, "Assets", "Tally.png"))) };
        var onMic = $"{prompt.App ?? "An app"} is using your microphone.";
        FrameworkElement content = prompt.Kind switch
        {
            CallPromptKind.SameMeeting => Ui.Column(8,
                Ui.Text("A new call started", "BodyStrongTextBlockStyle"),
                Ui.Text($"{onMic} Is it the meeting Tally just recorded?", brush: Ui.Secondary),
                Ui.Row(8, Ui.RedButton("Same Meeting", Ui.RecordGlyph, () => _ = model.SameMeeting()), Ui.Button("New Meeting", () => _ = model.NewMeeting()))),
            CallPromptKind.Recording => Ui.Column(8,
                Ui.Text("Tally is recording this call", "BodyStrongTextBlockStyle"),
                Ui.Text(onMic, brush: Ui.Secondary),
                Ui.Row(8, Ui.Button("Don't Record", model.DontRecord))),
            _ => Ui.Column(8,
                Ui.Text("Record this call with Tally?", "BodyStrongTextBlockStyle"),
                Ui.Text(onMic, brush: Ui.Secondary),
                Ui.Row(8, Ui.RedButton("Record", Ui.RecordGlyph, () => _ = model.RecordCall()), Ui.Button("Not Now", model.NotNow))),
        };
        Children.Clear();
        Children.Add(Ui.Grid("Auto,*", spacing: 12).Add(icon, content.At(1)));
        Ui.Name(this, prompt.Announcement);
    }
}
