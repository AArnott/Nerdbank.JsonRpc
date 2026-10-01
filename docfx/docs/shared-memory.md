# Shared memory transport

<xref:Nerdbank.JsonRpc.SharedMemoryDuplexPipe> connects two processes on the same machine through memory that both of them map. It implements <xref:System.IO.Pipelines.IDuplexPipe>, so it works with any channel, such as <xref:Nerdbank.JsonRpc.JsonRpcMessagePackChannel> or <xref:Nerdbank.JsonRpc.JsonRpcJsonChannel>.

Messages are not copied between the processes. The sender's serializer writes each message directly into the shared memory, and the receiver deserializes it from that same memory. A conventional transport such as a named pipe or socket instead copies every message into the kernel and back out again. The OS is involved only to wake up a peer that is waiting for data or for space.

## When to use it

Shared memory is a good choice when **all** of the following are true:

- Both processes run on the same machine.
- Both processes use Nerdbank.JsonRpc. The memory layout is specific to this library; other JSON-RPC implementations cannot connect to it.
- Messages are large (tens of kilobytes or more) or bandwidth is high. This is where avoiding copies pays off. In our benchmarks, a round trip carrying a large object graph took about half as long as over a named pipe, with less CPU time spent copying.
- You trust the other process as much as you trust your own user account. See [Security considerations](security.md#shared-memory).

Prefer a named pipe or socket when:

- Messages are small and infrequent. Each round trip still costs about one OS wake-up per message, just as with a named pipe, so small messages see little or no gain. A named pipe is simpler.
- The processes may run on different machines. Shared memory is local only.
- A peer is not trusted, or runs at a different privilege level.
- Many clients must connect to one well-known name. Each shared memory channel is a single connection between exactly two endpoints. To serve many clients, have each client negotiate a unique channel name with the server, for example over a conventional named pipe, and then connect to that channel.

The transport never spins (busy-waits) for data. A waiting reader or writer is parked until the OS wakes it up. This keeps CPU use low for the irregular traffic of most applications, at the cost of one wake-up per round trip.

## Connecting two processes

One process listens on a channel name:

[!code-csharp[](../../samples/cs/shared-memory.cs#listen)]

The other process connects using the same name:

[!code-csharp[](../../samples/cs/shared-memory.cs#connect)]

Choose a channel name that is unique and hard to guess, such as a new GUID. Pass it to the other process through a trusted path, for example on the command line of a child process that you start after <xref:Nerdbank.JsonRpc.SharedMemoryDuplexPipe.ListenAsync*> has begun.

<xref:Nerdbank.JsonRpc.SharedMemoryDuplexPipe.CreatePairAsync*> creates two connected endpoints within one process. That is useful for tests.

When either process disposes its endpoint, exits, or crashes, the other endpoint's reader completes, just as when a pipe is closed.

## Options

Customize the transport with <xref:Nerdbank.JsonRpc.SharedMemoryPipeOptions>. Both endpoints must use the same options.

[!code-csharp[](../../samples/cs/shared-memory.cs#options)]

- <xref:Nerdbank.JsonRpc.SharedMemoryPipeOptions.Capacity> is the size of the shared buffer in each direction (1 MB by default). It does not limit message size. Messages that fit in the free space are written in place. A larger message is buffered privately and copied in pieces as the reader makes room. If the writer gets far enough ahead of the reader to fill the buffer, flushing waits until the reader catches up. Physical memory is used only for the parts of the buffer that are actually written. Whenever the reader catches up, the writer starts again at the beginning, so a generous capacity is cheap unless it is actually filled.
- <xref:Nerdbank.JsonRpc.SharedMemoryPipeOptions.Signaling> selects how one endpoint wakes the other. The default, <xref:Nerdbank.JsonRpc.SharedMemorySignaling.Auto?displayProperty=nameWithType>, uses named events on Windows and a named pipe elsewhere.
- <xref:Nerdbank.JsonRpc.SharedMemoryPipeOptions.BaseDirectory> selects where the backing file is created on platforms other than Windows.

## Platform behavior

On Windows the shared memory is backed by the paging file, so no file is created. The OS frees the memory once both processes have closed it or exited, even if they crash.

The `netstandard2.0` assembly rejects this transport on all platforms because it cannot guarantee current-user-only rendezvous pipe security. Use the .NET Framework assembly on Windows or the .NET 8 or later assembly instead. On Linux and macOS, the shared memory is backed by a file, in the RAM-backed `/dev/shm` where it exists. The file is created so that only the current user can read or write it. After the client has fully initialized its mapping, it deletes the file before acknowledging the connection. If connection setup fails, the listener deletes the file instead.

On all platforms the two endpoints also share a named pipe. It is used to find each other, to detect when the peer goes away and, where named events are unavailable, to wake the peer.
