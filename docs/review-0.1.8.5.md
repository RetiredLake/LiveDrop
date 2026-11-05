# LiveDrop 0.1.8.5 cancellation fix

The regular-launch Cancel button previously reset the selection without calling the active send CancellationTokenSource. The captured transfer continued with its original offer. It now requests cancellation, disables repeat clicks, and shows Canceling until the task stops, then resets the selection with Share canceled. Progress callbacks check the current offer and cancellation state before updating the UI.

Socket read/write operations register cancellation to abort the underlying socket as well as cancel the WinRT operation. This prevents a blocked handshake or socket write from keeping the transfer alive. Disposal is idempotent, and canceled reads are normalized to OperationCanceledException. Quick Share no longer wraps user cancellation as a protocol failure. Microsoft Nearby forwards cancellation into connection establishment and preserves cancellation results.

Focused isolated-loopback tests verify that a blocked handshake read terminates, cancellation after the first chunk stops an outgoing 4 MiB file, the receiver terminates, and neither the partial nor final incomplete file remains. Normal multi-file and native-link round trips still pass. These are desktop tests, not a claim of a two-phone hardware test.

The notification layout from 0.1.8.4 remains: filename, progress bar, then Sending/Receiving and percentage. Older builds retain the existing text fallback. Version 0.1.8.0–0.1.8.4 release artifacts remain unchanged.
