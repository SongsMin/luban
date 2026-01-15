using Luban.Utils;

namespace Luban.TableSignature;

public static class NameParser
{
    public static string EnumNameByFullName(string fullName)
    {
        return fullName.Replace('.', '_');
    }

    public static string FullNameByPath(string path)
    {
        return path.Split('.')[0].Replace('/', '.').Replace('\\', '.');
    }
}
