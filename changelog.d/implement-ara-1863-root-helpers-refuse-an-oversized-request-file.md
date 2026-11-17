### Security

- **Root storage and update helpers refuse an oversized request file** (daemon packaging, #1863): `storage-request.sh` and `update-request.sh` now refuse a request file over 4096 bytes before reading it, as record 0017 rule 4 requires.
