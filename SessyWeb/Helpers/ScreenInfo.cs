namespace SessyWeb.Helpers
{
    public enum ScreenOrientation { Portrait, Landscape }

    public enum Breakpoint { Xs, Sm, Md, Lg, Xl, Xxl }

    public sealed class ScreenInfo
    {
        // Raw viewport (CSS px)
        public int Width { get; private set; }
        public int Height { get; private set; }

        // Kleine drempel om iOS-adresbalk-schommelingen te negeren
        private const int Noise = 4;

        public ScreenOrientation ScreenOrientation => Width >= Height ? ScreenOrientation.Landscape : ScreenOrientation.Portrait;

        public Breakpoint Breakpoint => Width switch
        {
            < 576 => Breakpoint.Xs,   // phones
            < 768 => Breakpoint.Sm,   // phones (grote)
            < 992 => Breakpoint.Md,   // tablets
            < 1200 => Breakpoint.Lg,   // small laptops
            < 1400 => Breakpoint.Xl,
            _ => Breakpoint.Xxl
        };

        // Handige flags
        public bool IsPhone => Breakpoint <= Breakpoint.Sm;   // < 768
        public bool IsTablet => Breakpoint == Breakpoint.Md;   // 768–991
        public bool IsMobile => Breakpoint <= Breakpoint.Sm;   // phone + tablet
        public bool IsLandscape => ScreenOrientation == ScreenOrientation.Landscape;

        public void Update(int width, int height)
        {
            if (Math.Abs(width - Width) < Noise && Math.Abs(height - Height) < Noise)
                return; // negeer mini-wijzigingen (iOS toolbars)

            Width = width;
            Height = height;
        }

        public override string ToString()
            => $"Width: {Width}, Height: {Height}, BP: {Breakpoint}, Phone: {IsPhone}, Tablet: {IsTablet}, IsMobile: {IsMobile}, IsLandscape: {IsLandscape}";
    }
}
