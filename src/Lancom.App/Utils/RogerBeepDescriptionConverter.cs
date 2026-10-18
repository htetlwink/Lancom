using System;
using System.Globalization;
using System.Windows.Data;
using Lancom.Core.Dsp;

namespace Lancom.App.Utils;

public class RogerBeepDescriptionConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is RogerBeepType type)
        {
            return type switch
            {
                RogerBeepType.None => "None (Muted)",
                RogerBeepType.MotorolaTactical => "Motorola Tactical (Classic Chirp)",
                RogerBeepType.NasaQuindar => "NASA Apollo (Quindar Beep)",
                RogerBeepType.ShortChirp => "Short Tactical Pip",
                RogerBeepType.HarrisTriTone => "Harris Falcon (Tri-Tone)",
                RogerBeepType.Mdc1200Police => "MDC-1200 (Police Data Burst)",
                RogerBeepType.AviationDoublePip => "Aviation (Double-Pip)",
                RogerBeepType.KenwoodTwoTone => "Kenwood (Two-Tone Alert)",
                RogerBeepType.HamRepeater => "Ham Radio (Courtesy Tone)",
                RogerBeepType.CyberpunkSweep => "Cyberpunk (Cyber Sweep)",
                RogerBeepType.UscmMarine => "Aliens - USCM Colonial Marine",
                RogerBeepType.ImperialStormtrooper => "Star Wars - Imperial Stormtrooper",
                RogerBeepType.ColonialViper => "Battlestar Galactica - Colonial Viper",
                RogerBeepType.StarfleetComm => "Star Trek - Starfleet Communicator",
                RogerBeepType.Hal9000 => "2001: Space Odyssey - HAL 9000",
                RogerBeepType.BladeRunnerSpinner => "Blade Runner - Spinner Radio",
                _ => value.ToString() ?? string.Empty
            };
        }
        return value?.ToString() ?? string.Empty;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
