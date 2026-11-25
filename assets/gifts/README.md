# Gift assets

Keep each gift's media in a directory named after its stable gift ID:

```text
assets/gifts/<gift-id>/
  animation.<format>
  poster.<format>
```

The poster is optional for formats that render a preview themselves. Reference the files from the gift definition in `test.html`. `windows/BuildInterface.ps1` packages this directory recursively, preserving its paths.
