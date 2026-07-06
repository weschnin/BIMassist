namespace BIMassist.Models
{
    public class BoreholeManagerSettings
    {
        public double BoreholeDiameterCm { get; set; } = 30.0;
        public string MaterialNameColumn { get; set; } = "Description";

        public BoreholeManagerSettings Clone()
        {
            return new BoreholeManagerSettings
            {
                BoreholeDiameterCm = BoreholeDiameterCm,
                MaterialNameColumn = MaterialNameColumn
            };
        }
    }
}
