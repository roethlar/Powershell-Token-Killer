#define _POSIX_C_SOURCE 200809L
#if defined(__APPLE__)
#define _DARWIN_C_SOURCE 1
#endif

#include <errno.h>
#include <fcntl.h>
#include <inttypes.h>
#include <poll.h>
#include <signal.h>
#include <stdbool.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/stat.h>
#include <sys/types.h>
#include <sys/wait.h>
#include <time.h>
#include <unistd.h>

#if defined(__APPLE__)
#include <sys/proc.h>
#include <libproc.h>
#endif

#define PTK_TERM_TO_KILL_MILLISECONDS 2000
#define PTK_CONTAINMENT_DEADLINE_MILLISECONDS 10000
#define PTK_IDENTITY_POLL_MILLISECONDS 25
#define PTK_MESSAGE_MAGIC UINT32_C(0x50544b52)

enum barrier_kind {
    BARRIER_HOST_GATED = 1,
    BARRIER_BEFORE_PENDING = 2,
    BARRIER_DURING_MOVE = 3,
    BARRIER_BEFORE_ARMED_ACK = 4,
    BARRIER_AFTER_ARMED_ACK = 5,
    BARRIER_AFTER_RELEASE_COMMAND = 6,
    BARRIER_AFTER_RELEASE = 7
};

enum message_kind {
    MESSAGE_HOST_GATED = 1,
    MESSAGE_RELEASE_HOST = 2,
    MESSAGE_WORKER_GATED = 3,
    MESSAGE_PENDING_ACK = 4,
    MESSAGE_MOVE_WORKER = 5,
    MESSAGE_WORKER_MOVED = 6,
    MESSAGE_CONTINUE_AFTER_MOVE = 7,
    MESSAGE_ARM_REQUEST = 8,
    MESSAGE_ARMED_ACK = 9,
    MESSAGE_CONTINUE_AFTER_ARMED_ACK = 10,
    MESSAGE_RELEASE_COMMAND = 11,
    MESSAGE_RELEASE_COMMAND_RECEIVED = 12,
    MESSAGE_OPEN_GATE = 13,
    MESSAGE_USER_RELEASED = 14,
    MESSAGE_MOVE_STARTED = 15,
    MESSAGE_CONTINUE_MOVE = 16
};

enum registry_state {
    REGISTRY_NONE = 0,
    REGISTRY_PENDING = 1,
    REGISTRY_ARMED = 2
};

struct process_identity {
    uint64_t high;
    uint64_t low;
};

struct fixture_message {
    uint32_t magic;
    int32_t kind;
    int32_t broker_pid;
    int32_t worker_pid;
    int32_t descendant_pid;
    int32_t process_group;
    struct process_identity broker_identity;
    struct process_identity worker_identity;
    struct process_identity descendant_identity;
};

struct ready_message {
    uint32_t magic;
    int32_t barrier;
    int32_t registry;
    int32_t guardian_pid;
    int32_t guardian_process_group;
    int32_t broker_pid;
    int32_t broker_process_group;
    int32_t host_pid;
    int32_t host_process_group;
    int32_t worker_broker_pid;
    int32_t worker_broker_process_group;
    int32_t worker_pid;
    int32_t worker_process_group;
    int32_t descendant_pid;
    int32_t liveness_writers;
    struct process_identity host_identity;
    struct process_identity worker_broker_identity;
    struct process_identity worker_identity;
    struct process_identity descendant_identity;
};

struct observed_processes {
    pid_t host_pid;
    struct process_identity host_identity;
    pid_t worker_broker_pid;
    struct process_identity worker_broker_identity;
    pid_t worker_pid;
    struct process_identity worker_identity;
    pid_t descendant_pid;
    struct process_identity descendant_identity;
    pid_t worker_process_group;
    enum registry_state registry;
};

static const char *global_marker_path;

static void fail_now(const char *operation)
{
    int saved_errno = errno;
    (void)dprintf(STDERR_FILENO, "fixture failure: %s errno=%d\n", operation, saved_errno);
    _exit(70);
}

static void close_checked(int descriptor)
{
    if (descriptor >= 0 && close(descriptor) != 0 && errno != EINTR) {
        fail_now("close");
    }
}

static void close_quietly(int descriptor)
{
    if (descriptor < 0) {
        return;
    }
    while (close(descriptor) != 0 && errno == EINTR) {
    }
}

static void write_full(int descriptor, const void *buffer, size_t length)
{
    const unsigned char *bytes = (const unsigned char *)buffer;
    size_t offset = 0U;
    while (offset < length) {
        ssize_t written = write(descriptor, bytes + offset, length - offset);
        if (written < 0 && errno == EINTR) {
            continue;
        }
        if (written <= 0) {
            fail_now("write");
        }
        offset += (size_t)written;
    }
}

static bool read_full_or_eof(int descriptor, void *buffer, size_t length)
{
    unsigned char *bytes = (unsigned char *)buffer;
    size_t offset = 0U;
    while (offset < length) {
        ssize_t received = read(descriptor, bytes + offset, length - offset);
        if (received < 0 && errno == EINTR) {
            continue;
        }
        if (received < 0) {
            fail_now("read");
        }
        if (received == 0) {
            if (offset == 0U) {
                return false;
            }
            errno = EPROTO;
            fail_now("short read");
        }
        offset += (size_t)received;
    }
    return true;
}

static struct fixture_message message_of(enum message_kind kind)
{
    struct fixture_message message;
    (void)memset(&message, 0, sizeof(message));
    message.magic = PTK_MESSAGE_MAGIC;
    message.kind = (int32_t)kind;
    return message;
}

static void send_message(int descriptor, const struct fixture_message *message)
{
    write_full(descriptor, message, sizeof(*message));
}

static struct fixture_message receive_message(int descriptor, enum message_kind expected)
{
    struct fixture_message message;
    if (!read_full_or_eof(descriptor, &message, sizeof(message))) {
        errno = EPIPE;
        fail_now("message eof");
    }
    if (message.magic != PTK_MESSAGE_MAGIC || message.kind != (int32_t)expected) {
        errno = EPROTO;
        fail_now("unexpected message");
    }
    return message;
}

static uint64_t monotonic_milliseconds(void)
{
    struct timespec value;
    if (clock_gettime(CLOCK_MONOTONIC, &value) != 0) {
        fail_now("clock_gettime");
    }
    return ((uint64_t)value.tv_sec * UINT64_C(1000)) +
        ((uint64_t)value.tv_nsec / UINT64_C(1000000));
}

static void sleep_milliseconds(uint64_t milliseconds)
{
    struct timespec request;
    request.tv_sec = (time_t)(milliseconds / UINT64_C(1000));
    request.tv_nsec = (long)((milliseconds % UINT64_C(1000)) * UINT64_C(1000000));
    while (nanosleep(&request, &request) != 0) {
        if (errno != EINTR) {
            fail_now("nanosleep");
        }
    }
}

static void sleep_until(uint64_t deadline)
{
    for (;;) {
        uint64_t now = monotonic_milliseconds();
        if (now >= deadline) {
            return;
        }
        uint64_t remaining = deadline - now;
        sleep_milliseconds(remaining < UINT64_C(25) ? remaining : UINT64_C(25));
    }
}

static bool identities_equal(
    struct process_identity left,
    struct process_identity right)
{
    return left.high == right.high && left.low == right.low;
}

#if defined(__APPLE__)
static bool read_process_identity(
    pid_t process_id,
    struct process_identity *identity,
    bool *is_zombie)
{
    struct proc_bsdinfo information;
    int received = proc_pidinfo(
        process_id,
        PROC_PIDTBSDINFO,
        0,
        &information,
        (int)sizeof(information));
    if (received != (int)sizeof(information)) {
        return false;
    }
    identity->high = (uint64_t)information.pbi_start_tvsec;
    identity->low = (uint64_t)information.pbi_start_tvusec;
    *is_zombie = information.pbi_status == SZOMB;
    return identity->high != UINT64_C(0) || identity->low != UINT64_C(0);
}
#else
static bool read_process_identity(
    pid_t process_id,
    struct process_identity *identity,
    bool *is_zombie)
{
    char path[64];
    int path_length = snprintf(path, sizeof(path), "/proc/%jd/stat", (intmax_t)process_id);
    if (path_length <= 0 || (size_t)path_length >= sizeof(path)) {
        return false;
    }

    int descriptor = open(path, O_RDONLY | O_CLOEXEC);
    if (descriptor < 0) {
        return false;
    }
    char buffer[4096];
    ssize_t received;
    do {
        received = read(descriptor, buffer, sizeof(buffer) - 1U);
    } while (received < 0 && errno == EINTR);
    close_quietly(descriptor);
    if (received <= 0) {
        return false;
    }
    buffer[(size_t)received] = '\0';

    char *right_parenthesis = strrchr(buffer, ')');
    if (right_parenthesis == NULL || right_parenthesis[1] != ' ') {
        return false;
    }
    char *cursor = right_parenthesis + 2;
    char *save_pointer = NULL;
    char *token = strtok_r(cursor, " ", &save_pointer);
    unsigned int token_index = 1U;
    char state = '\0';
    uint64_t start_ticks = UINT64_C(0);
    while (token != NULL) {
        if (token_index == 1U) {
            state = token[0];
        }
        if (token_index == 20U) {
            char *end = NULL;
            errno = 0;
            unsigned long long parsed = strtoull(token, &end, 10);
            if (errno != 0 || end == token || *end != '\0') {
                return false;
            }
            start_ticks = (uint64_t)parsed;
            break;
        }
        token = strtok_r(NULL, " ", &save_pointer);
        ++token_index;
    }
    if (start_ticks == UINT64_C(0)) {
        return false;
    }
    identity->high = UINT64_C(0);
    identity->low = start_ticks;
    *is_zombie = state == 'Z';
    return true;
}
#endif

static struct process_identity require_identity(pid_t process_id)
{
    uint64_t deadline = monotonic_milliseconds() + UINT64_C(2000);
    for (;;) {
        struct process_identity identity;
        bool zombie = false;
        if (read_process_identity(process_id, &identity, &zombie)) {
            return identity;
        }
        if (monotonic_milliseconds() >= deadline) {
            errno = ESRCH;
            fail_now("capture identity");
        }
        sleep_milliseconds(UINT64_C(1));
    }
}

static bool identity_is_live(
    pid_t process_id,
    struct process_identity expected,
    bool *is_zombie)
{
    if (process_id <= 0) {
        *is_zombie = false;
        return false;
    }
    struct process_identity actual;
    bool zombie = false;
    if (!read_process_identity(process_id, &actual, &zombie) ||
        !identities_equal(actual, expected)) {
        *is_zombie = false;
        return false;
    }
    *is_zombie = zombie;
    return true;
}

static void require_process_group(pid_t process_id, pid_t expected_group)
{
    pid_t actual_group = getpgid(process_id);
    if (actual_group != expected_group) {
        errno = EPROTO;
        fail_now("unexpected process group");
    }
}

static void ignore_term(void)
{
    if (signal(SIGTERM, SIG_IGN) == SIG_ERR) {
        fail_now("signal SIGTERM");
    }
}

static void redirect_diagnostics_to_null(void)
{
    int null_descriptor = open("/dev/null", O_WRONLY | O_CLOEXEC);
    if (null_descriptor < 0) {
        fail_now("open /dev/null");
    }
    if (dup2(null_descriptor, STDOUT_FILENO) < 0 ||
        dup2(null_descriptor, STDERR_FILENO) < 0) {
        fail_now("dup2 /dev/null");
    }
    if (null_descriptor > STDERR_FILENO) {
        close_checked(null_descriptor);
    }
}

static void worker_main(int gate_read, int status_write, int inherited_command, int inherited_event)
{
    close_quietly(inherited_command);
    close_quietly(inherited_event);
    ignore_term();

    struct fixture_message gated = message_of(MESSAGE_WORKER_GATED);
    gated.worker_pid = (int32_t)getpid();
    gated.worker_identity = require_identity(getpid());
    gated.process_group = (int32_t)getpgrp();
    send_message(status_write, &gated);

    unsigned char release = 0U;
    if (!read_full_or_eof(gate_read, &release, 1U) || release != UINT8_C(1)) {
        errno = EPROTO;
        fail_now("worker gate");
    }
    close_checked(gate_read);

    int marker = open(global_marker_path, O_WRONLY | O_CREAT | O_EXCL, S_IRUSR | S_IWUSR);
    if (marker < 0) {
        fail_now("create marker");
    }
    static const char marker_bytes[] = "user-entered\n";
    write_full(marker, marker_bytes, sizeof(marker_bytes) - 1U);
    if (fsync(marker) != 0) {
        fail_now("fsync marker");
    }
    close_checked(marker);

    int descendant_ready[2];
    if (pipe(descendant_ready) != 0) {
        fail_now("descendant pipe");
    }
    pid_t descendant = fork();
    if (descendant < 0) {
        fail_now("fork descendant");
    }
    if (descendant == 0) {
        close_quietly(descendant_ready[0]);
        close_quietly(status_write);
        ignore_term();
        unsigned char ready = UINT8_C(1);
        write_full(descendant_ready[1], &ready, 1U);
        close_checked(descendant_ready[1]);
        for (;;) {
            pause();
        }
    }

    close_checked(descendant_ready[1]);
    unsigned char descendant_signal = 0U;
    if (!read_full_or_eof(descendant_ready[0], &descendant_signal, 1U) ||
        descendant_signal != UINT8_C(1)) {
        errno = EPROTO;
        fail_now("descendant ready");
    }
    close_checked(descendant_ready[0]);

    struct fixture_message released = message_of(MESSAGE_USER_RELEASED);
    released.worker_pid = (int32_t)getpid();
    released.worker_identity = require_identity(getpid());
    released.process_group = (int32_t)getpgrp();
    released.descendant_pid = (int32_t)descendant;
    released.descendant_identity = require_identity(descendant);
    send_message(status_write, &released);
    close_checked(status_write);

    for (;;) {
        pause();
    }
}

static void worker_broker_main(
    int command_read,
    int event_write,
    int inherited_host_command,
    int inherited_host_event)
{
    close_quietly(inherited_host_command);
    close_quietly(inherited_host_event);
    ignore_term();

    int gate[2];
    int worker_status[2];
    if (pipe(gate) != 0 || pipe(worker_status) != 0) {
        fail_now("worker broker pipes");
    }

    pid_t worker = fork();
    if (worker < 0) {
        fail_now("fork worker");
    }
    if (worker == 0) {
        close_quietly(gate[1]);
        close_quietly(worker_status[0]);
        worker_main(gate[0], worker_status[1], command_read, event_write);
    }

    close_checked(gate[0]);
    close_checked(worker_status[1]);
    struct fixture_message worker_gated = receive_message(
        worker_status[0],
        MESSAGE_WORKER_GATED);
    require_process_group(worker, getpgrp());

    struct fixture_message gated = message_of(MESSAGE_WORKER_GATED);
    gated.broker_pid = (int32_t)getpid();
    gated.broker_identity = require_identity(getpid());
    gated.worker_pid = (int32_t)worker;
    gated.worker_identity = worker_gated.worker_identity;
    gated.process_group = (int32_t)getpgrp();
    send_message(event_write, &gated);

    (void)receive_message(command_read, MESSAGE_MOVE_WORKER);
    if (setpgid(worker, worker) != 0) {
        fail_now("setpgid worker");
    }
    require_process_group(worker, worker);
    struct fixture_message move_started = gated;
    move_started.kind = MESSAGE_MOVE_STARTED;
    move_started.process_group = (int32_t)worker;
    send_message(event_write, &move_started);
    (void)receive_message(command_read, MESSAGE_CONTINUE_MOVE);
    struct fixture_message moved = gated;
    moved.kind = MESSAGE_WORKER_MOVED;
    moved.process_group = (int32_t)worker;
    send_message(event_write, &moved);

    (void)receive_message(command_read, MESSAGE_RELEASE_COMMAND);
    struct fixture_message release_received = moved;
    release_received.kind = MESSAGE_RELEASE_COMMAND_RECEIVED;
    send_message(event_write, &release_received);

    (void)receive_message(command_read, MESSAGE_OPEN_GATE);
    unsigned char release = UINT8_C(1);
    write_full(gate[1], &release, 1U);
    close_checked(gate[1]);

    struct fixture_message released = receive_message(
        worker_status[0],
        MESSAGE_USER_RELEASED);
    released.broker_pid = (int32_t)getpid();
    released.broker_identity = require_identity(getpid());
    send_message(event_write, &released);
    close_checked(worker_status[0]);

    for (;;) {
        pause();
    }
}

static void host_main(
    int command_read,
    int event_write,
    int inherited_liveness,
    int inherited_ready)
{
    close_quietly(inherited_liveness);
    close_quietly(inherited_ready);
    ignore_term();
    if (setpgid(0, 0) != 0) {
        fail_now("setpgid host");
    }
    require_process_group(getpid(), getpid());

    struct fixture_message host_gated = message_of(MESSAGE_HOST_GATED);
    host_gated.worker_pid = (int32_t)getpid();
    host_gated.worker_identity = require_identity(getpid());
    host_gated.process_group = (int32_t)getpgrp();
    send_message(event_write, &host_gated);
    (void)receive_message(command_read, MESSAGE_RELEASE_HOST);

    int broker_command[2];
    int broker_event[2];
    if (pipe(broker_command) != 0 || pipe(broker_event) != 0) {
        fail_now("per-worker broker pipes");
    }
    pid_t worker_broker = fork();
    if (worker_broker < 0) {
        fail_now("fork worker broker");
    }
    if (worker_broker == 0) {
        close_quietly(broker_command[1]);
        close_quietly(broker_event[0]);
        worker_broker_main(
            broker_command[0],
            broker_event[1],
            command_read,
            event_write);
    }

    close_checked(broker_command[0]);
    close_checked(broker_event[1]);
    struct fixture_message worker_gated = receive_message(
        broker_event[0],
        MESSAGE_WORKER_GATED);
    if (worker_gated.broker_pid != (int32_t)worker_broker) {
        errno = EPROTO;
        fail_now("worker broker pid mismatch");
    }
    require_process_group(worker_broker, getpid());
    require_process_group((pid_t)worker_gated.worker_pid, getpid());
    send_message(event_write, &worker_gated);

    (void)receive_message(command_read, MESSAGE_PENDING_ACK);
    struct fixture_message move = message_of(MESSAGE_MOVE_WORKER);
    send_message(broker_command[1], &move);
    struct fixture_message move_started = receive_message(
        broker_event[0],
        MESSAGE_MOVE_STARTED);
    send_message(event_write, &move_started);
    (void)receive_message(command_read, MESSAGE_CONTINUE_MOVE);
    struct fixture_message continue_move = message_of(MESSAGE_CONTINUE_MOVE);
    send_message(broker_command[1], &continue_move);
    struct fixture_message moved = receive_message(
        broker_event[0],
        MESSAGE_WORKER_MOVED);
    send_message(event_write, &moved);

    (void)receive_message(command_read, MESSAGE_CONTINUE_AFTER_MOVE);
    struct fixture_message arm = moved;
    arm.kind = MESSAGE_ARM_REQUEST;
    send_message(event_write, &arm);
    (void)receive_message(command_read, MESSAGE_ARMED_ACK);
    (void)receive_message(command_read, MESSAGE_CONTINUE_AFTER_ARMED_ACK);

    struct fixture_message release = message_of(MESSAGE_RELEASE_COMMAND);
    send_message(broker_command[1], &release);
    struct fixture_message release_received = receive_message(
        broker_event[0],
        MESSAGE_RELEASE_COMMAND_RECEIVED);
    send_message(event_write, &release_received);

    (void)receive_message(command_read, MESSAGE_OPEN_GATE);
    struct fixture_message open_gate = message_of(MESSAGE_OPEN_GATE);
    send_message(broker_command[1], &open_gate);
    struct fixture_message released = receive_message(
        broker_event[0],
        MESSAGE_USER_RELEASED);
    send_message(event_write, &released);

    for (;;) {
        pause();
    }
}

static const char *barrier_name(enum barrier_kind barrier)
{
    switch (barrier) {
        case BARRIER_HOST_GATED: return "host_gated";
        case BARRIER_BEFORE_PENDING: return "before_pending";
        case BARRIER_DURING_MOVE: return "during_move";
        case BARRIER_BEFORE_ARMED_ACK: return "before_armed_ack";
        case BARRIER_AFTER_ARMED_ACK: return "after_armed_ack";
        case BARRIER_AFTER_RELEASE_COMMAND: return "after_release_command";
        case BARRIER_AFTER_RELEASE: return "after_release";
        default: return "invalid";
    }
}

static const char *registry_name(enum registry_state state)
{
    switch (state) {
        case REGISTRY_NONE: return "none";
        case REGISTRY_PENDING: return "pending";
        case REGISTRY_ARMED: return "armed";
        default: return "invalid";
    }
}

static bool wait_for_liveness_eof(int liveness_read)
{
    unsigned char value = 0U;
    for (;;) {
        ssize_t received = read(liveness_read, &value, 1U);
        if (received < 0 && errno == EINTR) {
            continue;
        }
        if (received < 0) {
            fail_now("liveness read");
        }
        return received == 0;
    }
}

static void signal_one_identity(
    pid_t process_id,
    struct process_identity identity,
    int signal_number)
{
    bool zombie = false;
    if (identity_is_live(process_id, identity, &zombie) && !zombie) {
        if (kill(process_id, signal_number) != 0 && errno != ESRCH) {
            fail_now("signal process");
        }
    }
}

static void signal_identity_group(
    pid_t leader,
    struct process_identity identity,
    int signal_number)
{
    bool zombie = false;
    if (!identity_is_live(leader, identity, &zombie) || zombie) {
        return;
    }
    if (getpgid(leader) != leader) {
        return;
    }
    if (kill(-leader, signal_number) != 0 && errno != ESRCH) {
        fail_now("signal process group");
    }
}

/*
 * The host is this broker's unreaped direct child and established PGID == PID
 * before release. Its numeric PID therefore cannot be reused while this broker
 * retains parentage, so the creation-time host group can be signaled at the
 * exact deadline without a post-deadline identity query.
 */
static void signal_direct_host_group(pid_t host_pid, int signal_number)
{
    if (host_pid <= 0) {
        return;
    }
    if (kill(-host_pid, signal_number) != 0 && errno != ESRCH) {
        fail_now("signal direct host group");
    }
}

static bool reap_direct_host(pid_t host_pid)
{
    int status = 0;
    pid_t result;
    do {
        result = waitpid(host_pid, &status, WNOHANG);
    } while (result < 0 && errno == EINTR);
    if (result == 0) {
        return false;
    }
    if (result != host_pid) {
        fail_now("waitpid direct host");
    }
    return true;
}

static int count_live_nonchildren(
    const struct observed_processes *observed,
    int *zombies,
    int *identity_polls,
    bool *polled_worker_broker,
    bool *polled_worker,
    bool *polled_descendant)
{
    int live = 0;
    *zombies = 0;
    if (observed->worker_broker_pid > 0) {
        bool zombie = false;
        ++*identity_polls;
        *polled_worker_broker = true;
        if (identity_is_live(
                observed->worker_broker_pid,
                observed->worker_broker_identity,
                &zombie)) {
            ++live;
            if (zombie) {
                ++*zombies;
            }
        }
    }
    if (observed->worker_pid > 0) {
        bool zombie = false;
        ++*identity_polls;
        *polled_worker = true;
        if (identity_is_live(
                observed->worker_pid,
                observed->worker_identity,
                &zombie)) {
            ++live;
            if (zombie) {
                ++*zombies;
            }
        }
    }
    if (observed->descendant_pid > 0) {
        bool zombie = false;
        ++*identity_polls;
        *polled_descendant = true;
        if (identity_is_live(
                observed->descendant_pid,
                observed->descendant_identity,
                &zombie)) {
            ++live;
            if (zombie) {
                ++*zombies;
            }
        }
    }
    return live;
}

static bool group_exists(pid_t process_group)
{
    if (process_group <= 0) {
        return false;
    }
    if (kill(-process_group, 0) == 0 || errno == EPERM) {
        return true;
    }
    return false;
}

static void write_transcript(
    const char *transcript_path,
    enum barrier_kind barrier,
    const struct observed_processes *observed,
    uint64_t term_at,
    uint64_t kill_at,
    int identity_polls,
    bool polled_worker_broker,
    bool polled_worker,
    bool polled_descendant,
    bool host_alive_after_term,
    int nonchildren_alive_after_term,
    uint64_t completed_at,
    int waitpid_calls,
    int survivors,
    int zombies,
    bool host_group_gone,
    bool worker_group_gone)
{
    int descriptor = open(
        transcript_path,
        O_WRONLY | O_CREAT | O_EXCL | O_CLOEXEC,
        S_IRUSR | S_IWUSR);
    if (descriptor < 0) {
        fail_now("open transcript");
    }
    int result = dprintf(
        descriptor,
        "{\"barrier\":\"%s\",\"registry\":\"%s\","
        "\"termAtMs\":%" PRIu64 ",\"killAtMs\":%" PRIu64 ","
        "\"termToKillMs\":%d,\"deadlineMs\":%d,\"pollMs\":%d,"
        "\"waitpidCalls\":%d,\"waitpidTarget\":%jd,"
        "\"nonChildWaitpidCalls\":0,\"identityPolls\":%d,"
        "\"polledWorkerBroker\":%s,\"polledWorker\":%s,"
        "\"polledDescendant\":%s,\"hostAliveAfterTerm\":%s,"
        "\"nonchildrenAliveAfterTerm\":%d,\"completedAtMs\":%" PRIu64 ","
        "\"hostGroupGone\":%s,"
        "\"workerGroupGone\":%s,\"survivors\":%d,\"zombies\":%d}\n",
        barrier_name(barrier),
        registry_name(observed->registry),
        term_at,
        kill_at,
        PTK_TERM_TO_KILL_MILLISECONDS,
        PTK_CONTAINMENT_DEADLINE_MILLISECONDS,
        PTK_IDENTITY_POLL_MILLISECONDS,
        waitpid_calls,
        (intmax_t)observed->host_pid,
        identity_polls,
        polled_worker_broker ? "true" : "false",
        polled_worker ? "true" : "false",
        polled_descendant ? "true" : "false",
        host_alive_after_term ? "true" : "false",
        nonchildren_alive_after_term,
        completed_at,
        host_group_gone ? "true" : "false",
        worker_group_gone ? "true" : "false",
        survivors,
        zombies);
    if (result < 0 || fsync(descriptor) != 0) {
        fail_now("write transcript");
    }
    close_checked(descriptor);
}

static void contain_and_report(
    int liveness_read,
    int host_command_write,
    const char *transcript_path,
    enum barrier_kind barrier,
    struct observed_processes *observed)
{
    (void)wait_for_liveness_eof(liveness_read);
    close_checked(liveness_read);

    uint64_t started = monotonic_milliseconds();
    uint64_t term_at = monotonic_milliseconds() - started;
    signal_direct_host_group(observed->host_pid, SIGTERM);
    if (observed->worker_process_group > 0) {
        signal_identity_group(
            observed->worker_pid,
            observed->worker_identity,
            SIGTERM);
    }
    signal_one_identity(
        observed->worker_broker_pid,
        observed->worker_broker_identity,
        SIGTERM);

    /* Observe TERM resistance before the absolute KILL boundary. */
    bool host_zombie_after_term = false;
    bool host_alive_after_term = identity_is_live(
        observed->host_pid,
        observed->host_identity,
        &host_zombie_after_term);
    int prekill_zombies = 0;
    int prekill_identity_polls = 0;
    bool prekill_polled_worker_broker = false;
    bool prekill_polled_worker = false;
    bool prekill_polled_descendant = false;
    int nonchildren_alive_after_term = count_live_nonchildren(
        observed,
        &prekill_zombies,
        &prekill_identity_polls,
        &prekill_polled_worker_broker,
        &prekill_polled_worker,
        &prekill_polled_descendant);

    sleep_until(started + (uint64_t)PTK_TERM_TO_KILL_MILLISECONDS);
    uint64_t kill_at = monotonic_milliseconds() - started;
    signal_direct_host_group(observed->host_pid, SIGKILL);
    if (observed->worker_process_group > 0) {
        signal_identity_group(
            observed->worker_pid,
            observed->worker_identity,
            SIGKILL);
    }
    signal_one_identity(
        observed->worker_broker_pid,
        observed->worker_broker_identity,
        SIGKILL);

    /* Keep the creation protocol blocked through the TERM grace period. */
    close_quietly(host_command_write);

    int identity_polls = 0;
    int waitpid_calls = 0;
    int survivors = 0;
    int zombies = 0;
    bool polled_worker_broker = false;
    bool polled_worker = false;
    bool polled_descendant = false;
    bool host_group_gone = false;
    bool worker_group_gone = observed->worker_process_group <= 0;
    bool host_reaped = false;
    uint64_t deadline = started + (uint64_t)PTK_CONTAINMENT_DEADLINE_MILLISECONDS;
    for (;;) {
        if (!host_reaped) {
            ++waitpid_calls;
            host_reaped = reap_direct_host(observed->host_pid);
        }
        survivors = count_live_nonchildren(
            observed,
            &zombies,
            &identity_polls,
            &polled_worker_broker,
            &polled_worker,
            &polled_descendant);
        host_group_gone = !group_exists(observed->host_pid);
        worker_group_gone = observed->worker_process_group <= 0 ||
            !group_exists(observed->worker_process_group);
        if (host_reaped && survivors == 0 && host_group_gone && worker_group_gone) {
            break;
        }
        uint64_t now = monotonic_milliseconds();
        if (now >= deadline) {
            break;
        }
        uint64_t remaining = deadline - now;
        sleep_milliseconds(remaining < (uint64_t)PTK_IDENTITY_POLL_MILLISECONDS
            ? remaining
            : (uint64_t)PTK_IDENTITY_POLL_MILLISECONDS);
    }

    write_transcript(
        transcript_path,
        barrier,
        observed,
        term_at,
        kill_at,
        identity_polls,
        polled_worker_broker,
        polled_worker,
        polled_descendant,
        host_alive_after_term && !host_zombie_after_term,
        nonchildren_alive_after_term,
        monotonic_milliseconds() - started,
        waitpid_calls,
        survivors,
        zombies,
        host_group_gone,
        worker_group_gone);
    _exit((host_reaped && survivors == 0 && zombies == 0 &&
        host_group_gone && worker_group_gone) ? 0 : 74);
}

static void write_ready_snapshot(
    int ready_write,
    enum barrier_kind barrier,
    const struct observed_processes *observed)
{
    struct ready_message ready;
    (void)memset(&ready, 0, sizeof(ready));
    ready.magic = PTK_MESSAGE_MAGIC;
    ready.barrier = (int32_t)barrier;
    ready.registry = (int32_t)observed->registry;
    ready.guardian_pid = (int32_t)getppid();
    ready.broker_pid = (int32_t)getpid();
    ready.broker_process_group = (int32_t)getpgrp();
    ready.host_pid = (int32_t)observed->host_pid;
    ready.host_process_group = (int32_t)observed->host_pid;
    ready.worker_broker_pid = (int32_t)observed->worker_broker_pid;
    ready.worker_broker_process_group = observed->worker_broker_pid > 0
        ? (int32_t)getpgid(observed->worker_broker_pid)
        : 0;
    ready.worker_pid = (int32_t)observed->worker_pid;
    ready.worker_process_group = observed->worker_pid > 0
        ? (int32_t)getpgid(observed->worker_pid)
        : 0;
    ready.descendant_pid = (int32_t)observed->descendant_pid;
    ready.liveness_writers = 1;
    ready.host_identity = observed->host_identity;
    ready.worker_broker_identity = observed->worker_broker_identity;
    ready.worker_identity = observed->worker_identity;
    ready.descendant_identity = observed->descendant_identity;
    write_full(ready_write, &ready, sizeof(ready));
    close_checked(ready_write);
}

static void publish_barrier(
    int ready_write,
    int liveness_read,
    int host_command_write,
    const char *transcript_path,
    enum barrier_kind barrier,
    const struct observed_processes *observed)
{
    write_ready_snapshot(ready_write, barrier, observed);

    struct observed_processes mutable_observed = *observed;
    contain_and_report(
        liveness_read,
        host_command_write,
        transcript_path,
        barrier,
        &mutable_observed);
}

static struct fixture_message receive_host_event(
    int event_read,
    enum message_kind expected,
    int liveness_read,
    int host_command_write,
    const char *transcript_path,
    enum barrier_kind barrier,
    struct observed_processes *observed)
{
    struct pollfd descriptors[2];
    descriptors[0].fd = liveness_read;
    descriptors[0].events = POLLIN | POLLHUP;
    descriptors[0].revents = 0;
    descriptors[1].fd = event_read;
    descriptors[1].events = POLLIN | POLLHUP;
    descriptors[1].revents = 0;

    for (;;) {
        int result = poll(descriptors, 2U, -1);
        if (result < 0 && errno == EINTR) {
            continue;
        }
        if (result < 0) {
            fail_now("poll host event");
        }
        if ((descriptors[0].revents & (POLLIN | POLLHUP | POLLERR | POLLNVAL)) != 0) {
            unsigned char unexpected = 0U;
            ssize_t received;
            do {
                received = read(liveness_read, &unexpected, 1U);
            } while (received < 0 && errno == EINTR);
            if (received == 0) {
                contain_and_report(
                    liveness_read,
                    host_command_write,
                    transcript_path,
                    barrier,
                    observed);
                errno = EPROTO;
                fail_now("containment returned");
            }
            errno = EPROTO;
            fail_now("unexpected liveness data");
        }
        if ((descriptors[1].revents & (POLLIN | POLLHUP | POLLERR | POLLNVAL)) != 0) {
            return receive_message(event_read, expected);
        }
    }
}

static void outer_broker_main(
    int liveness_read,
    int ready_write,
    const char *transcript_path,
    enum barrier_kind barrier,
    bool stall_on_host_event)
{
    ignore_term();
    redirect_diagnostics_to_null();

    int host_command[2];
    int host_event[2];
    if (pipe(host_command) != 0 || pipe(host_event) != 0) {
        fail_now("host pipes");
    }

    pid_t host = fork();
    if (host < 0) {
        fail_now("fork host");
    }
    if (host == 0) {
        close_quietly(host_command[1]);
        close_quietly(host_event[0]);
        host_main(host_command[0], host_event[1], liveness_read, ready_write);
    }

    close_checked(host_command[0]);
    close_checked(host_event[1]);
    if (setpgid(host, host) != 0 && errno != EACCES) {
        fail_now("parent setpgid host");
    }

    struct observed_processes observed;
    (void)memset(&observed, 0, sizeof(observed));
    observed.host_pid = host;
    observed.host_identity = require_identity(host);
    observed.registry = REGISTRY_NONE;

    struct fixture_message host_gated = receive_host_event(
        host_event[0],
        MESSAGE_HOST_GATED,
        liveness_read,
        host_command[1],
        transcript_path,
        barrier,
        &observed);
    if (host_gated.worker_pid != (int32_t)host ||
        !identities_equal(host_gated.worker_identity, observed.host_identity)) {
        errno = EPROTO;
        fail_now("host identity mismatch");
    }
    require_process_group(host, host);
    if (getpgrp() == host) {
        errno = EPROTO;
        fail_now("outer broker joined host group");
    }
    if (stall_on_host_event) {
        write_ready_snapshot(ready_write, barrier, &observed);
        (void)receive_host_event(
            host_event[0],
            MESSAGE_WORKER_GATED,
            liveness_read,
            host_command[1],
            transcript_path,
            barrier,
            &observed);
        errno = EPROTO;
        fail_now("stalled host unexpectedly advanced");
    }
    if (barrier == BARRIER_HOST_GATED) {
        publish_barrier(
            ready_write,
            liveness_read,
            host_command[1],
            transcript_path,
            barrier,
            &observed);
    }

    struct fixture_message release_host = message_of(MESSAGE_RELEASE_HOST);
    send_message(host_command[1], &release_host);
    struct fixture_message worker_gated = receive_host_event(
        host_event[0],
        MESSAGE_WORKER_GATED,
        liveness_read,
        host_command[1],
        transcript_path,
        barrier,
        &observed);
    observed.worker_broker_pid = (pid_t)worker_gated.broker_pid;
    observed.worker_broker_identity = worker_gated.broker_identity;
    observed.worker_pid = (pid_t)worker_gated.worker_pid;
    observed.worker_identity = worker_gated.worker_identity;
    if (!identity_is_live(
            observed.worker_broker_pid,
            observed.worker_broker_identity,
            &(bool){ false }) ||
        !identity_is_live(
            observed.worker_pid,
            observed.worker_identity,
            &(bool){ false })) {
        errno = EPROTO;
        fail_now("worker identity mismatch");
    }
    require_process_group(observed.worker_broker_pid, host);
    require_process_group(observed.worker_pid, host);
    if (barrier == BARRIER_BEFORE_PENDING) {
        publish_barrier(
            ready_write,
            liveness_read,
            host_command[1],
            transcript_path,
            barrier,
            &observed);
    }

    observed.registry = REGISTRY_PENDING;
    observed.worker_process_group = observed.worker_pid;
    struct fixture_message pending_ack = message_of(MESSAGE_PENDING_ACK);
    send_message(host_command[1], &pending_ack);
    struct fixture_message move_started = receive_host_event(
        host_event[0],
        MESSAGE_MOVE_STARTED,
        liveness_read,
        host_command[1],
        transcript_path,
        barrier,
        &observed);
    if (move_started.worker_pid != (int32_t)observed.worker_pid ||
        !identities_equal(move_started.worker_identity, observed.worker_identity)) {
        errno = EPROTO;
        fail_now("move-start identity mismatch");
    }
    if (barrier == BARRIER_DURING_MOVE) {
        publish_barrier(
            ready_write,
            liveness_read,
            host_command[1],
            transcript_path,
            barrier,
            &observed);
    }

    struct fixture_message continue_move_command = message_of(MESSAGE_CONTINUE_MOVE);
    send_message(host_command[1], &continue_move_command);
    struct fixture_message moved = receive_host_event(
        host_event[0],
        MESSAGE_WORKER_MOVED,
        liveness_read,
        host_command[1],
        transcript_path,
        barrier,
        &observed);
    if (moved.worker_pid != (int32_t)observed.worker_pid ||
        !identities_equal(moved.worker_identity, observed.worker_identity)) {
        errno = EPROTO;
        fail_now("moved identity mismatch");
    }
    require_process_group(observed.worker_pid, observed.worker_pid);

    struct fixture_message continue_move = message_of(MESSAGE_CONTINUE_AFTER_MOVE);
    send_message(host_command[1], &continue_move);
    struct fixture_message arm = receive_host_event(
        host_event[0],
        MESSAGE_ARM_REQUEST,
        liveness_read,
        host_command[1],
        transcript_path,
        barrier,
        &observed);
    if (arm.worker_pid != (int32_t)observed.worker_pid ||
        !identities_equal(arm.worker_identity, observed.worker_identity)) {
        errno = EPROTO;
        fail_now("arm identity mismatch");
    }
    require_process_group(observed.worker_pid, observed.worker_pid);
    observed.registry = REGISTRY_ARMED;
    if (barrier == BARRIER_BEFORE_ARMED_ACK) {
        publish_barrier(
            ready_write,
            liveness_read,
            host_command[1],
            transcript_path,
            barrier,
            &observed);
    }

    struct fixture_message armed_ack = message_of(MESSAGE_ARMED_ACK);
    send_message(host_command[1], &armed_ack);
    if (barrier == BARRIER_AFTER_ARMED_ACK) {
        publish_barrier(
            ready_write,
            liveness_read,
            host_command[1],
            transcript_path,
            barrier,
            &observed);
    }

    struct fixture_message continue_armed = message_of(MESSAGE_CONTINUE_AFTER_ARMED_ACK);
    send_message(host_command[1], &continue_armed);
    struct fixture_message release_received = receive_host_event(
        host_event[0],
        MESSAGE_RELEASE_COMMAND_RECEIVED,
        liveness_read,
        host_command[1],
        transcript_path,
        barrier,
        &observed);
    if (release_received.worker_pid != (int32_t)observed.worker_pid) {
        errno = EPROTO;
        fail_now("release identity mismatch");
    }
    if (barrier == BARRIER_AFTER_RELEASE_COMMAND) {
        publish_barrier(
            ready_write,
            liveness_read,
            host_command[1],
            transcript_path,
            barrier,
            &observed);
    }

    struct fixture_message open_gate = message_of(MESSAGE_OPEN_GATE);
    send_message(host_command[1], &open_gate);
    struct fixture_message released = receive_host_event(
        host_event[0],
        MESSAGE_USER_RELEASED,
        liveness_read,
        host_command[1],
        transcript_path,
        barrier,
        &observed);
    if (released.worker_pid != (int32_t)observed.worker_pid ||
        !identities_equal(released.worker_identity, observed.worker_identity)) {
        errno = EPROTO;
        fail_now("released identity mismatch");
    }
    observed.descendant_pid = (pid_t)released.descendant_pid;
    observed.descendant_identity = released.descendant_identity;
    require_process_group(observed.descendant_pid, observed.worker_pid);
    publish_barrier(
        ready_write,
        liveness_read,
        host_command[1],
        transcript_path,
        barrier,
        &observed);
}

static enum barrier_kind parse_barrier(const char *value)
{
    if (strcmp(value, "host_gated") == 0) return BARRIER_HOST_GATED;
    if (strcmp(value, "before_pending") == 0) return BARRIER_BEFORE_PENDING;
    if (strcmp(value, "during_move") == 0) return BARRIER_DURING_MOVE;
    if (strcmp(value, "before_armed_ack") == 0) return BARRIER_BEFORE_ARMED_ACK;
    if (strcmp(value, "after_armed_ack") == 0) return BARRIER_AFTER_ARMED_ACK;
    if (strcmp(value, "after_release_command") == 0) return BARRIER_AFTER_RELEASE_COMMAND;
    if (strcmp(value, "after_release") == 0) return BARRIER_AFTER_RELEASE;
    return (enum barrier_kind)0;
}

static int sentinel_main(void)
{
    (void)printf("{\"pid\":%jd}\n", (intmax_t)getpid());
    if (fflush(stdout) != 0) {
        return 70;
    }
    for (;;) {
        pause();
    }
}

static int identity_fence_main(const char *value)
{
    char *end = NULL;
    errno = 0;
    intmax_t parsed = strtoimax(value, &end, 10);
    if (errno != 0 || end == value || *end != '\0' ||
        parsed <= 0 || parsed > INT32_MAX) {
        return 64;
    }

    pid_t target = (pid_t)parsed;
    struct process_identity actual = require_identity(target);
    struct process_identity corrupted = actual;
    corrupted.low ^= UINT64_C(1);
    signal_one_identity(target, corrupted, SIGKILL);

    /* A bypassed identity comparison has ample time to deliver SIGKILL. */
    sleep_milliseconds(UINT64_C(100));
    bool zombie = false;
    bool target_alive = identity_is_live(target, actual, &zombie) && !zombie;
    (void)printf(
        "{\"targetPid\":%jd,\"corruptedIdentityRejected\":%s,"
        "\"targetAlive\":%s}\n",
        (intmax_t)target,
        target_alive ? "true" : "false",
        target_alive ? "true" : "false");
    if (fflush(stdout) != 0) {
        return 70;
    }
    return target_alive ? 0 : 75;
}

int main(int argc, char **argv)
{
    if (argc == 2 && strcmp(argv[1], "sentinel") == 0) {
        return sentinel_main();
    }
    if (argc == 3 && strcmp(argv[1], "identity-fence") == 0) {
        return identity_fence_main(argv[2]);
    }
    if (argc != 5 ||
        (strcmp(argv[1], "guardian") != 0 &&
            strcmp(argv[1], "guardian-stalled") != 0)) {
        return 64;
    }
    bool stall_on_host_event = strcmp(argv[1], "guardian-stalled") == 0;
    enum barrier_kind barrier = parse_barrier(argv[2]);
    if (barrier == (enum barrier_kind)0 || argv[3][0] != '/' || argv[4][0] != '/') {
        return 64;
    }
    global_marker_path = argv[4];

    /*
     * Keep every fixture-owned process out of the test runner's process
     * group. The outer broker inherits this group, while the host and worker
     * each establish their own containment groups below.
     */
    if (getpgrp() != getpid() && setpgid(0, 0) != 0) {
        return 70;
    }

    int liveness[2];
    int ready[2];
    if (pipe(liveness) != 0 || pipe(ready) != 0) {
        return 70;
    }

    pid_t broker = fork();
    if (broker < 0) {
        return 70;
    }
    if (broker == 0) {
        close_quietly(liveness[1]);
        close_quietly(ready[0]);
        outer_broker_main(
            liveness[0],
            ready[1],
            argv[3],
            barrier,
            stall_on_host_event);
    }

    close_checked(liveness[0]);
    close_checked(ready[1]);
    struct ready_message message;
    if (!read_full_or_eof(ready[0], &message, sizeof(message)) ||
        message.magic != PTK_MESSAGE_MAGIC ||
        message.barrier != (int32_t)barrier ||
        message.broker_pid != (int32_t)broker) {
        return 70;
    }
    close_checked(ready[0]);
    message.guardian_pid = (int32_t)getpid();
    message.guardian_process_group = (int32_t)getpgrp();

    (void)printf(
        "{\"barrier\":\"%s\",\"registry\":\"%s\","
        "\"guardianPid\":%d,\"guardianPgid\":%d,\"brokerPid\":%d,"
        "\"brokerPgid\":%d,\"hostPid\":%d,\"hostPgid\":%d,"
        "\"workerBrokerPid\":%d,\"workerBrokerPgid\":%d,"
        "\"workerPid\":%d,\"workerPgid\":%d,"
        "\"descendantPid\":%d,\"livenessWriters\":%d}\n",
        barrier_name(barrier),
        registry_name((enum registry_state)message.registry),
        message.guardian_pid,
        message.guardian_process_group,
        message.broker_pid,
        message.broker_process_group,
        message.host_pid,
        message.host_process_group,
        message.worker_broker_pid,
        message.worker_broker_process_group,
        message.worker_pid,
        message.worker_process_group,
        message.descendant_pid,
        message.liveness_writers);
    if (fflush(stdout) != 0) {
        return 70;
    }

    for (;;) {
        pause();
    }
}
