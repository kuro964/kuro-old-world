using System.Globalization;

namespace Content.Client._Stalker_OW.SlotMachine;

public static class SlotMachineFormat
{
    public static string Number(int value) => value.ToString("N0", CultureInfo.InvariantCulture);
    
    public static string Percent(double fraction) => (fraction * 100d).ToString("0.##", CultureInfo.InvariantCulture);
}
