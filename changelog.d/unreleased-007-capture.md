### Fixed (capture)

- Frames whose values all fit in 0-255 (for example a short bias from a camera with a low black level) are saved with their real values. Before, when the camera server sent such a frame in the narrow Alpaca ImageBytes Byte format, Ara multiplied every pixel by 256. Frames already saved this way are exact: divide each value by 256.
