LiveDrop 0.1.4.11 is a Quick Share UKEY2 interoperability hotfix.

- Encodes P-256 public-key coordinates as positive big-endian two's-complement values, including the required leading zero when a coordinate's high bit is set. This prevents Android Quick Share from rejecting the UKEY2 initiation.
- Retains the corrected Quick Share control-payload marker, endpoint-info device-type flags, static Bluetooth trigger, automatic receive acceptance, visible receive storage, and clean final-file shutdown from 0.1.4.10.

The release is intended for testing between LiveDrop clients and Quick Share. Physical Android testing is still required to confirm the complete exchange on the target device.
