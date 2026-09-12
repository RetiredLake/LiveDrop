LiveDrop 0.1.4.8 is a Quick Share transfer hotfix.

Changes:

- Corrected the Quick Share client/server handshake order.
- The receiver sends the plaintext connection response first, then sends its paired-key encryption frame before reading the sender's paired-key response.
- The sender waits for those receiver frames before replying and sending the introduction.
- Keep-alive writes begin only after the encrypted session has completed the plaintext connection response.

This fixes the pre-introduction deadlock that prevented LiveDrop from reaching the consent and file-transfer stages with official Quick Share clients and between LiveDrop instances.

Validation:

- x64 and ARM Release builds completed with the WpBlueBubbles VS 2019/SDK toolchain.
- Protocol smoke tests pass.
- The package and combined ARM/x64 bundle are signed with certificate thumbprint `B90DCB33BC1AB90DBF79B8BBF34A2592D25315C0`.
