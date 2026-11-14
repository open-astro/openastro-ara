### Changed

- **Every profile section save can name the copy it loaded** (#1807): the 24 settings sections the app writes answer an `ETag` on read and write, and a save whose `If-Match` names another copy is refused with a 412 and writes nothing. A save with no `If-Match` still works for now. Storage's tag changes once.
