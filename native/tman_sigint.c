// Counts SIGINTs in signal context, so tman can ask synchronously whether one has arrived.
//
// .NET answers a SIGINT on a dispatch thread it starts for the purpose, which a loaded host can
// schedule after tman has already read the child's exit: a child that traps the same Ctrl+C and
// exits 0 then read as a finished run. The kernel runs this handler on delivery, and delivers the
// SIGINT before the SIGCHLD of a child that exits on it, so by the time tman sees that exit the
// count has moved. Managed code cannot be this handler: a reverse P/Invoke entered on a thread
// that was running managed code fails fast, and attaching an unknown thread allocates.
//
// Chained in front of whatever handler was installed (the runtime's), which still runs as before.
// sigaction, siginfo_t and SA_ONSTACK are POSIX/XSI, which a strict -std=c11 hides
#define _XOPEN_SOURCE 700
#include <signal.h>
#include <stddef.h>
#include <stdatomic.h>
#include <stdint.h>

static _Atomic int64_t received;
static struct sigaction previous;

static void count_sigint(int sig, siginfo_t *info, void *context)
{
    atomic_fetch_add_explicit(&received, 1, memory_order_relaxed);
    if (previous.sa_flags & SA_SIGINFO)
        previous.sa_sigaction(sig, info, context);
    else if (previous.sa_handler != SIG_DFL && previous.sa_handler != SIG_IGN)
        previous.sa_handler(sig);
}

// 0 once the counter is in front of the current handler (at once when it already is), else
// sigaction's -1 with errno set.
int tman_sigint_install(void)
{
    struct sigaction current;
    if (sigaction(SIGINT, NULL, &current) != 0) return -1;
    if ((current.sa_flags & SA_SIGINFO) && current.sa_sigaction == count_sigint) return 0;

    struct sigaction counting;
    counting.sa_sigaction = count_sigint;
    counting.sa_flags = SA_SIGINFO | (current.sa_flags & (SA_RESTART | SA_ONSTACK));
    counting.sa_mask = current.sa_mask;
    previous = current;
    return sigaction(SIGINT, &counting, NULL);
}

int64_t tman_sigint_received(void)
{
    return atomic_load_explicit(&received, memory_order_relaxed);
}
