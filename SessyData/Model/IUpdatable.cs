namespace SessyData.Model
{
    public interface IUpdatable<T>
        where T : class
    {
        public int Id { get; set; }

        void Update(T updateInfo)
        {

        }
    }
}