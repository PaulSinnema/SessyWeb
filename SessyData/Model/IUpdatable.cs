namespace SessyData.Model
{
    public interface IUpdatable<T>
        where T : class
    {
        public int Id { get; set; }

        abstract void Update(T updateInfo);
    }
}