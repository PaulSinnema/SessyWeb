namespace SessyWeb.Helpers
{
    public class ScreenInfo
    {
        public int Width { get; set; }
        public int Height { get; set; }
        public bool IsMobile => Width <= 1000;
        public bool IsLandscape => Width > Height;

        public override string ToString()
        {
            return $"Width: {Width}, Height: {Height}, IsMobile: {IsMobile}, IsLandscape: {IsLandscape}";
        }
    }
}
