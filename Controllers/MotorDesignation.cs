namespace Project_FREAK.Controllers;

public static class MotorDesignation
{
    public static string Format(double totalImpulseNewtonSeconds, double averageThrustNewtons)
    {
        char? impulseClass = totalImpulseNewtonSeconds switch
        {
            <= 2.5 => 'A',
            <= 5 => 'B',
            <= 10 => 'C',
            <= 20 => 'D',
            <= 40 => 'E',
            <= 80 => 'F',
            <= 160 => 'G',
            <= 320 => 'H',
            <= 640 => 'I',
            <= 1280 => 'J',
            <= 2560 => 'K',
            <= 5120 => 'L',
            <= 10240 => 'M',
            <= 20480 => 'N',
            <= 40960 => 'O',
            <= 81920 => 'P',
            _ => null
        };

        if (impulseClass is null)
            return $"Unclassified (above P range; {averageThrustNewtons:F0} N average thrust)";

        return $"{impulseClass}{averageThrustNewtons:F0}";
    }
}