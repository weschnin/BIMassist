namespace BIMassist.Models
{
    public class Borehole
    {
        public string LocationID { get; set; }
        public string LocationType { get; set; }
        public double Easting { get; set; }
        public double Northing { get; set; }
        public double GroundLevel { get; set; }
        public double FinalDepth { get; set; }
        public string Orientation { get; set; }
        public string Inclination { get; set; }

        public void CopyFrom(Borehole other)
        {
            LocationType = other.LocationType;
            Easting = other.Easting;
            Northing = other.Northing;
            GroundLevel = other.GroundLevel;
            FinalDepth = other.FinalDepth;
            Orientation = other.Orientation;
            Inclination = other.Inclination;
        }
    }
}
