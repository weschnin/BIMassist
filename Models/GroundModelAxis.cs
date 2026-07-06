namespace BIMassist.Models
{
    public class GroundModelAxis
    {
        public double X0 { get; set; }
        public double Y0 { get; set; }
        public double X1 { get; set; }
        public double Y1 { get; set; }

        public bool IsValid()
        {
            var dx = X1 - X0;
            var dy = Y1 - Y0;
            return (dx * dx + dy * dy) > 1e-8;
        }

        public GroundModelAxis Clone()
        {
            return new GroundModelAxis
            {
                X0 = X0,
                Y0 = Y0,
                X1 = X1,
                Y1 = Y1
            };
        }
    }
}
