namespace BIMassist.Models
{
    public class GroundModelBoundaryPoint
    {
        public double X { get; set; }
        public double Y { get; set; }

        public GroundModelBoundaryPoint Clone()
        {
            return new GroundModelBoundaryPoint { X = X, Y = Y };
        }
    }
}
