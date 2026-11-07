using SamaraLauncher.Core.Models;

namespace SamaraLauncher.Core.Java;

public interface IJavaLocator
{
    /// <summary>Ищет Java 17+ для инстанса. Бросает LauncherException, если не найдена.</summary>
    JavaInfo Find(InstanceProfile instance);

    /// <summary>
    /// Ищет Java нужной major-версии. allowNewer=false требует точного совпадения;
    /// true допускает более новую Java (например, 21 вместо 17).
    /// </summary>
    JavaInfo Find(InstanceProfile instance, int requiredMajor, bool allowNewer = true);
}
