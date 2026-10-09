namespace INP_IE.ConduitManager.Services
{
    public static class RevitUnitConverter
    {
        public const double FeetToMillimeters = 304.8;
        public const double MillimetersToFeet = 1.0 / FeetToMillimeters;

        public static double ToFeet(double millimeters)
        {
            return millimeters * MillimetersToFeet;
        }

        public static double ToMillimeters(double feet)
        {
            return feet * FeetToMillimeters;
        }
    }
}
