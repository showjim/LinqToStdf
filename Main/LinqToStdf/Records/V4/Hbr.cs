﻿using System;
using System.Collections.Generic;
using System.Text;

namespace LinqToStdf.Records.V4 {
    public class Hbr : BinSummaryRecord {

        public override RecordType RecordType {
            get { return new RecordType(1, 40); }
        }

        public override BinType BinType {
            get { return BinType.Hard; }
        }
    }
}