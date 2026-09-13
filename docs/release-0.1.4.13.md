LiveDrop 0.1.4.13 is a Quick Share transfer interoperability hotfix.

- Uses fresh positive payload IDs consistently in the introduction metadata and FILE payload headers.
- Sets `FileMetadata.id` to the same value as `payload_id`, as required by current Android Quick Share implementations.
- Includes `PayloadHeader.file_name` on outgoing file chunks.
- Resolves incoming files using either attachment identity field when the match is unambiguous and reports the received payload fields when it is not.
- Advertises and completes the safe terminal disconnection exchange with a bounded wait, preventing the receiver from closing the socket while the sender is still writing its final frame.
- Adds the current Quick Share keep-alive fields to connection requests and responses.

The package is signed with the current RetiredLake store certificate. Physical Android and Windows peer testing remains required for final interoperability confirmation.
