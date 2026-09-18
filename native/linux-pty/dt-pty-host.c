#if !defined(__APPLE__)
#define _GNU_SOURCE
#endif

#include <errno.h>
#include <fcntl.h>
#include <poll.h>
#if defined(__APPLE__)
#include <util.h>
#else
#include <pty.h>
#endif
#include <signal.h>
#include <stdbool.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/ioctl.h>
#include <sys/types.h>
#include <sys/wait.h>
#include <unistd.h>

static bool nonblocking(int fd) {
    int flags = fcntl(fd, F_GETFL);
    return flags >= 0 && fcntl(fd, F_SETFL, flags | O_NONBLOCK) == 0;
}

static bool retryable(void) {
    return errno == EINTR || errno == EAGAIN || errno == EWOULDBLOCK;
}

static void resize_pty(int master, unsigned columns, unsigned rows, unsigned xpixels, unsigned ypixels) {
    struct winsize size = {
        .ws_row = (unsigned short)rows,
        .ws_col = (unsigned short)columns,
        .ws_xpixel = (unsigned short)xpixels,
        .ws_ypixel = (unsigned short)ypixels,
    };
    ioctl(master, TIOCSWINSZ, &size);
}

int main(int argc, char **argv) {
    if (argc != 5) {
        fprintf(stderr, "usage: dt-pty-host <columns> <rows> <cwd> <command>\n");
        return 64;
    }

    unsigned columns = (unsigned)strtoul(argv[1], NULL, 10);
    unsigned rows = (unsigned)strtoul(argv[2], NULL, 10);
    if (columns == 0 || columns > UINT16_MAX || rows == 0 || rows > UINT16_MAX) {
        fprintf(stderr, "invalid terminal dimensions\n");
        return 64;
    }

    struct winsize size = {
        .ws_row = (unsigned short)rows,
        .ws_col = (unsigned short)columns,
    };
    int master = -1;
    pid_t child = forkpty(&master, NULL, NULL, &size);
    if (child < 0) {
        perror("forkpty");
        return 71;
    }

    if (child == 0) {
        setenv("TERM", getenv("TERM") ? getenv("TERM") : "xterm-256color", 0);
        if (argv[3][0] != '\0' && chdir(argv[3]) != 0) {
            perror("chdir");
            _exit(72);
        }

        execl("/bin/sh", "sh", "-lc", argv[4], (char *)NULL);
        perror("exec");
        _exit(127);
    }

    signal(SIGPIPE, SIG_IGN);
    if (!nonblocking(master) || !nonblocking(STDIN_FILENO) || !nonblocking(STDOUT_FILENO)) {
        perror("nonblocking relay");
        kill(-child, SIGKILL);
        close(master);
        waitpid(child, NULL, 0);
        return 74;
    }

    bool input_open = true;
    bool master_open = true;
    bool master_hungup = false;
    uint8_t input[16384], output[16384];
    size_t input_length = 0, input_offset = 0;
    size_t output_length = 0, output_offset = 0;
    size_t remaining = 0, header_length = 0;
    char header[96];
    bool failed = false;
    while (master_open || output_length > 0) {
        struct pollfd descriptors[3] = {
            { .fd = master_open && (output_length == 0 || (input_length > 0 && !master_hungup)) ? master : -1,
              .events = (output_length == 0 ? POLLIN : 0) | (input_length > 0 ? POLLOUT : 0) },
            { .fd = input_open && input_length == 0 ? STDIN_FILENO : -1, .events = POLLIN },
            { .fd = output_length > 0 ? STDOUT_FILENO : -1, .events = POLLOUT },
        };
        int result = poll(descriptors, 3, -1);
        if (result < 0) {
            if (errno == EINTR) continue;
            perror("poll");
            failed = true;
            break;
        }

        if (descriptors[0].revents & (POLLHUP | POLLERR)) master_hungup = true;

        if (output_length == 0 && descriptors[0].revents & (POLLIN | POLLHUP | POLLERR)) {
            ssize_t count = read(master, output, sizeof(output));
            if (count > 0) {
                output_length = (size_t)count;
                output_offset = 0;
            } else if (count == 0 || errno == EIO) {
                master_open = false;
            } else if (!retryable()) {
                perror("read pty");
                failed = true;
                break;
            }
        }

        if (descriptors[2].revents & (POLLOUT | POLLERR | POLLHUP)) {
            ssize_t count = write(STDOUT_FILENO, output + output_offset, output_length);
            if (count > 0) {
                output_offset += (size_t)count;
                output_length -= (size_t)count;
            } else if (count == 0 || !retryable()) {
                perror("write stdout");
                failed = true;
                break;
            }
        }

        if (master_open && input_length > 0 && descriptors[0].revents & POLLOUT) {
            ssize_t count = write(master, input + input_offset, input_length);
            if (count > 0) {
                input_offset += (size_t)count;
                input_length -= (size_t)count;
            } else if (count == 0 || !retryable()) {
                perror("write pty");
                failed = true;
                break;
            }
        }

        if (input_open && descriptors[1].revents & (POLLIN | POLLHUP)) {
            size_t capacity = remaining > 0
                ? (remaining < sizeof(input) ? remaining : sizeof(input)) : 1;
            ssize_t count = read(STDIN_FILENO, input, capacity);
            if (count == 0) {
                input_open = false;
                kill(-child, SIGHUP);
                continue;
            }

            if (count < 0) {
                if (retryable()) continue;
                perror("read stdin");
                failed = true;
                break;
            }

            if (remaining > 0) {
                input_length = (size_t)count;
                input_offset = 0;
                remaining -= (size_t)count;
                continue;
            }

            if (input[0] != '\n') {
                if (header_length + 1 >= sizeof(header)) {
                    fprintf(stderr, "input header too long\n");
                    failed = true;
                    break;
                }
                header[header_length++] = (char)input[0];
                continue;
            }

            header[header_length] = '\0';
            header_length = 0;
            if (header[0] == 'D' && header[1] == ' ') {
                char *end;
                errno = 0;
                unsigned long length = strtoul(header + 2, &end, 10);
                if (errno != 0 || end == header + 2 || *end != '\0' || length > 4 * 1024 * 1024) {
                    fprintf(stderr, "invalid input frame length\n");
                    failed = true;
                    break;
                }
                remaining = (size_t)length;
            } else if (header[0] == 'R' && header[1] == ' ') {
                unsigned new_columns = 0;
                unsigned new_rows = 0;
                unsigned xpixels = 0;
                unsigned ypixels = 0;
                int parsed = sscanf(header + 2, "%u %u %u %u", &new_columns, &new_rows, &xpixels, &ypixels);
                if (parsed >= 2 &&
                    new_columns > 0 && new_columns <= UINT16_MAX &&
                    new_rows > 0 && new_rows <= UINT16_MAX &&
                    xpixels <= UINT16_MAX &&
                    ypixels <= UINT16_MAX) {
                    resize_pty(master, new_columns, new_rows, xpixels, ypixels);
                }
            } else if (strcmp(header, "C") == 0) {
                input_open = false;
                kill(-child, SIGHUP);
            }
        }
    }

    if (failed) kill(-child, SIGKILL);
    close(master);
    int status = 0;
    while (waitpid(child, &status, 0) < 0 && errno == EINTR) {
    }

    if (failed) return 74;
    if (WIFEXITED(status)) return WEXITSTATUS(status);
    if (WIFSIGNALED(status)) return 128 + WTERMSIG(status);
    return 1;
}
