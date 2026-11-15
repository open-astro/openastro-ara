### Fixed (Guider)

- **A lost guide star now stops the light in progress** (#1891): the star-lost pause stops the frame being exposed and takes it again on Resume, so a light shot on a lost star is not saved. A pause you press yourself still lets the frame finish.
- **The run resumes only once guiding has settled** (#1891): a star found again far from the lock position no longer resumes the run or restarts the grace period. The run resumes after the guide error stays inside the profile's settle distance for its settle time, also after the long-outage recovery.
