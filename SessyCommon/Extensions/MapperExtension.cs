using SessyCommon.Attributes;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Reflection;

namespace SessyCommon.Extensions
{
    public static class MapperExtension
    {
        /// <summary>
        /// Copies data in properties from the source to the destination.
        /// </summary>
        /// <remarks>
        /// Properties having the [Key] or [NotMapped] attribute are not copied.
        /// </remarks>
        public static void Copy<T>(this T destination, T source)
            where T : class, new()
        {
            var type = typeof(T);
            var members = type.GetMembers(BindingFlags.Instance | BindingFlags.Public);

            foreach (var member in members)
            {
                PropertyInfo? memberInfo = type.GetProperty(member.Name);

                if (memberInfo != null)
                {
                    Attribute? key = memberInfo.GetCustomAttribute<KeyAttribute>();
                    Attribute? notMapped = memberInfo.GetCustomAttribute<NotMappedAttribute>();
                    Attribute? skipCopy = memberInfo.GetCustomAttribute<SkipCopyAttribute>();

                    if (memberInfo.MemberType == MemberTypes.Property &&
                        memberInfo.SetMethod != null &&
                        notMapped == null &&
                        skipCopy == null &&
                        key == null)
                    {
                        try
                        {
                            memberInfo.SetValue(destination, memberInfo.GetValue(source), null);
                        }
                        catch (Exception ex)
                        {
                            throw new Exception($"Error setting value for {destination.GetType().FullName} - {memberInfo.Name}", ex);
                        }
                    }
                }
            }
        }
    }
}
