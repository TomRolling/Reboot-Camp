namespace MaintenancePC.Services;

public static class Format
{
    /// <summary>Taille lisible en français : 1536 → « 1,5 Ko ».</summary>
    public static string Size(long bytes)
    {
        if (bytes < 1024) return $"{Math.Max(bytes, 0)} o";

        string[] units = ["Ko", "Mo", "Go", "To"];
        double value = bytes;
        int unit = -1;
        do
        {
            value /= 1024;
            unit++;
        } while (value >= 1024 && unit < units.Length - 1);

        return value >= 100 ? $"{value:0} {units[unit]}" : $"{value:0.#} {units[unit]}";
    }
}
