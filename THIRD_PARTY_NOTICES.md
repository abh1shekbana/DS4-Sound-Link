# Third-Party Software Notices and Licenses

This application incorporates or references components from the following open-source projects.

---

## 1. NAudio & NAudio.Wasapi

- **Project**: NAudio (Audio library for .NET)
- **Repository**: https://github.com/naudio/NAudio
- **Author**: Mark Heath and Contributors
- **License**: MIT License

```
MIT License

Copyright (c) 2020 Mark Heath

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

---

## 2. SbcSharp (DualShock 4 SBC Subband Codec Implementation)

- **Project**: SbcSharp / DS4AudioStreamer
- **Repositories**: 
  - https://github.com/asimonf/DS4AudioStreamer
  - https://github.com/nefarius/DS4AudioStreamer
- **Authors**: Simon M. (asimonf), Benjamin Höglinger-Stelzer (Nefarius)
- **License**: Apache License, Version 2.0

```
                                 Apache License
                           Version 2.0, January 2004
                        http://www.apache.org/licenses/

TERMS AND CONDITIONS FOR USE, REPRODUCTION, AND DISTRIBUTION

1. Definitions.
   "License" shall mean the terms and conditions for use, reproduction,
   and distribution as defined by Sections 1 through 9 of this document.
   "Licensor" shall mean the copyright owner or entity authorized by
   the copyright owner that is granting the License.
   "Legal Entity" shall mean the union of the acting entity and all
   other entities that control, are controlled by, or are under common
   control with that entity.
   "You" (or "Your") shall mean an individual or Legal Entity exercising
   permissions granted by this License.
   "Source" form shall mean the preferred form for making modifications.
   "Object" form shall mean any form resulting from mechanical
   transformation or translation of a Source form.
   "Work" shall mean the work of authorship made available under the License.
   "Derivative Works" shall mean any work based upon (or derived from) the
   Work and for which the editorial revisions, annotations, elaborations,
   or other modifications represent, as a whole, an original work of authorship.

2. Grant of Copyright License. Subject to the terms and conditions of this
   License, each Contributor hereby grants to You a perpetual, worldwide,
   non-exclusive, no-charge, royalty-free, irrevocable copyright license to
   reproduce, prepare Derivative Works of, publicly display, publicly perform,
   sublicense, and distribute the Work and such Derivative Works in Source or
   Object form.

3. Grant of Patent License. Subject to the terms and conditions of this License,
   each Contributor hereby grants to You a perpetual, worldwide, non-exclusive,
   no-charge, royalty-free, irrevocable patent license to make, have made, use,
   offer to sell, sell, import, and otherwise transfer the Work.

4. Redistribution. You may reproduce and distribute copies of the Work or
   Derivative Works thereof in any medium, with or without modifications, and
   in Source or Object form, provided that You meet the following conditions:
   (a) You must give any other recipients of the Work or Derivative Works a copy
       of this License; and
   (b) You must cause any modified files to carry prominent notices stating that
       You changed the files; and
   (c) You must retain, in the Source form of any Derivative Works that You
       distribute, all copyright, patent, trademark, and attribution notices; and
   (d) If the Work includes a "NOTICE" text file as part of its distribution,
       then any Derivative Works that You distribute must include a readable
       copy of the attribution notices contained within such NOTICE file.
```

---

## 3. Microsoft .NET Runtime and Framework Libraries

- **Project**: .NET (Core / Runtime / WPF / Windows Forms)
- **Repository**: https://github.com/dotnet/runtime
- **Author**: .NET Foundation and Contributors
- **License**: MIT License

---

## 4. DSZ (DualSense & DualShock Protocol Reference)

- **Project**: DSZ (`crates/ds-proto`, `crates/dsz`)
- **Repository**: https://github.com/MaximumFreak/dsz
- **Author**: MaximumFreak and Contributors
- **License**: MIT License

```
MIT License

Copyright (c) 2024 MaximumFreak

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

Protocol constants, Report 0x31 structure, DualSense audio path configuration (`0x00` headset vs `0x30` internal speaker), and physical 3.5 mm jack detection bitflags referenced from DSZ and Linux kernel `hid-playstation` driver under permissible open-source terms.

