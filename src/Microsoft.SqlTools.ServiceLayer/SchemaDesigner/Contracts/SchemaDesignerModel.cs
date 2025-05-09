//
// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
//

using System.Collections.Generic;

namespace Microsoft.SqlTools.ServiceLayer.SchemaDesigner
{
    public class SchemaDesignerModel
    {
        /// <summary>
        /// Gets or sets the entities (Table in MSSQL) in the schema
        /// </summary>
        public List<SchemaDesignerTable>? Tables { get; set; }
    }
}