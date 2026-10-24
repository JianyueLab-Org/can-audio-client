// ARM64 CAN Audio.app 的入口，exec Contents/MacOS/arm64/ControllerClient。
// 语音引擎放在同目录下的 controller/can-voice。
// exec 不换进程，输入监控、麦克风权限照样记在这个 .app 名下。
#include <libgen.h>
#include <limits.h>
#include <mach-o/dyld.h>
#include <stdio.h>
#include <stdlib.h>
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
    snprintf(target, sizeof target, "%s/arm64/ControllerClient", dir);
    if (access(target, X_OK) != 0) {
        perror("CAN Audio: ControllerClient");
        return 1;
    }
    argv[0] = target;
    execv(target, argv);
    perror("CAN Audio: execv");
    return 1;
}
