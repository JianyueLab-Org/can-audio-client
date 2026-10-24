// CAN Audio.app 的入口：通用二进制（arm64 + x86_64），按本机架构 exec 对应目录里的程序。
// .NET 单文件程序和 PyInstaller 打出的语音引擎都不能合成一个通用二进制，所以两套各放一个目录：
//   Contents/MacOS/arm64/ControllerClient、Contents/MacOS/x64/ControllerClient（各自带 controller/can-voice）
// exec 不换进程，输入监控、麦克风权限照样记在这个 .app 名下。
#include <libgen.h>
#include <limits.h>
#include <mach-o/dyld.h>
#include <stdio.h>
#include <string.h>
#include <unistd.h>

int main(int argc, char *argv[])
{
    (void)argc;
    char path[PATH_MAX], real[PATH_MAX], dir[PATH_MAX], target[PATH_MAX];
    uint32_t size = sizeof path;
    if (_NSGetExecutablePath(path, &size) != 0 || realpath(path, real) == NULL) {
        fprintf(stderr, "CAN Audio: cannot locate executable\n");
        return 1;
    }
    strncpy(dir, dirname(real), sizeof dir - 1);
    dir[sizeof dir - 1] = '\0';
#if defined(__arm64__) || defined(__aarch64__)
    const char *order[] = {"arm64", "x64"};
#else
    const char *order[] = {"x64", "arm64"};
#endif
    for (int i = 0; i < 2; i++) {
        snprintf(target, sizeof target, "%s/%s/ControllerClient", dir, order[i]);
        if (access(target, X_OK) == 0) {
            argv[0] = target;
            execv(target, argv);
            perror("CAN Audio: execv");
            return 1;
        }
    }
    fprintf(stderr, "CAN Audio: no ControllerClient for this architecture in %s\n", dir);
    return 1;
}
