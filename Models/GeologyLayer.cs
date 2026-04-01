using System;

namespace BIMassist.Models
{
    public class GeologyLayer
    {
        public string LocationID { get; set; }
        public double DepthTop { get; set; }
        public double DepthBase { get; set; }
        public string Description { get; set; }
        public string Colour { get; set; }
        public string Consistency { get; set; }
        public string Classification { get; set; }
        public string GeologyCode { get; set; }

        public void CopyFrom(GeologyLayer other)
        {
            DepthTop = other.DepthTop;
            DepthBase = other.DepthBase;
            Description = other.Description;
            Colour = other.Colour;
            Consistency = other.Consistency;
            Classification = other.Classification;
            GeologyCode = other.GeologyCode;
        }

        public bool EqualsByIdentity(GeologyLayer o)
            => o != null
               && LocationID == o.LocationID
               && Math.Abs(DepthTop - o.DepthTop) < 1e-9
               && Math.Abs(DepthBase - o.DepthBase) < 1e-9
               && (Description ?? "") == (o.Description ?? "");
    }
}
