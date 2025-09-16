using SessyCommon.Attributes;
using SessyCommon.Extensions;
using System.Collections;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SessyUnitTests
{
    public class MapperTests
    {
        public class MapTesterClass
        {
            [Key]
            public int Id { get; set; }
            public string? Name { get; set; }
            public string? Description { get; set; }
            [NotMapped]
            public int NotMapped { get; set; }
            [SkipCopy]
            public double Skipped { get; set; }
        }

        public class MapTesterData : IEnumerable<object[]>
        {
            public IEnumerator<object[]> GetEnumerator()
            {
                yield return new object[] { new MapTesterClass { Id = 1, Name = "Test1", Description = "Description 1", NotMapped = 1, Skipped = 1.0 } };
                yield return new object[] { new MapTesterClass { Id = 2, Name = "Test2", Description = "Description 2", NotMapped = 2, Skipped = 2.0 } };
                yield return new object[] { new MapTesterClass { Id = 3, Name = "Test3", Description = null, NotMapped = 3, Skipped = 3.0 } };
            }

            IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        }

        [Theory]
        [ClassData(typeof(MapTesterData))]
        public void MapTest1(MapTesterClass expected)
        {
            // Arrange
            var actual = new MapTesterClass();
            // Act
            actual.Copy(expected);

            // Assert
            Assert.True(expected.Id > 0);
            Assert.Equal(0, actual.Id);
            Assert.NotEqual(string.Empty, actual.Name);
            Assert.Equal(expected.Name, actual.Name);
            Assert.Equal(expected.Description, actual.Description);
            Assert.Equal(0, actual.NotMapped);
            Assert.Equal(0.0, actual.Skipped);
        }
    }
}
