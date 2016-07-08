﻿namespace FSharp.Data

open System
open System.Collections.Generic
open System.Data
open System.Data.SqlClient
open System.Diagnostics
open System.IO
open System.Reflection
open System.Runtime.Caching
open System.Data.SqlTypes
open Microsoft.FSharp.Core.CompilerServices
open Microsoft.FSharp.Quotations

open Microsoft.SqlServer.Server

open ProviderImplementation.ProvidedTypes

open FSharp.Data.SqlClient

[<TypeProvider>]
type public SqlProgrammabilityProvider(config : TypeProviderConfig) as this = 
    inherit TypeProviderForNamespaces()

    let assembly = Assembly.LoadFrom( config.RuntimeAssembly)
    let nameSpace = this.GetType().Namespace
    let providerType = ProvidedTypeDefinition(assembly, nameSpace, "SqlProgrammabilityProvider", Some typeof<obj>, HideObjectMethods = true)

    let cache = new MemoryCache(name = this.GetType().Name)
    let methodsCache = new MemoryCache(name = this.GetType().Name)

    do 
        this.Disposing.Add <| fun _ -> 
            cache.Dispose()
            methodsCache.Dispose()
            clearDataTypesMap()
    do 
        //this.RegisterRuntimeAssemblyLocationAsProbingFolder( config) 

        providerType.DefineStaticParameters(
            parameters = [ 
                ProvidedStaticParameter("ConnectionStringOrName", typeof<string>) 
                ProvidedStaticParameter("ConfigFile", typeof<string>, "") 
                ProvidedStaticParameter("DataDirectory", typeof<string>, "") 
                ProvidedStaticParameter("UseReturnValue", typeof<bool>, false) 
            ],             
            instantiationFunction = (fun typeName args ->
                let root = lazy this.CreateRootType(typeName, unbox args.[0], unbox args.[1], unbox args.[2], unbox args.[3])
                cache.GetOrAdd(typeName, root)
            ) 
        )

        providerType.AddXmlDoc """
<summary>Typed access to SQL Server programmable objects: stored procedures, functions and user defined table types.</summary> 
<param name='ConnectionStringOrName'>String used to open a SQL Server database or the name of the connection string in the configuration file in the form of “name=&lt;connection string name&gt;”.</param>
<param name='ResultType'>A value that defines structure of result: Records, Tuples, DataTable, or SqlDataReader.</param>
<param name='ConfigFile'>The name of the configuration file that’s used for connection strings at DESIGN-TIME. The default value is app.config or web.config.</param>
<param name='DataDirectory'>The name of the data directory that replaces |DataDirectory| in connection strings. The default value is the project or script directory.</param>
"""

        this.AddNamespace(nameSpace, [ providerType ])
    
    override this.ResolveAssembly args = 
        config.ReferencedAssemblies 
        |> Array.tryFind (fun x -> AssemblyName.ReferenceMatchesDefinition(AssemblyName.GetAssemblyName x, AssemblyName args.Name)) 
        |> Option.map Assembly.LoadFrom
        |> defaultArg 
        <| base.ResolveAssembly args

    member internal this.CreateRootType( typeName, connectionStringOrName, configFile, dataDirectory, useReturnValue) =
        if String.IsNullOrWhiteSpace connectionStringOrName then invalidArg "ConnectionStringOrName" "Value is empty!" 
        
        let designTimeConnectionString = DesignTimeConnectionString.Parse(connectionStringOrName, config.ResolutionFolder, configFile)

        let dataDirectoryFullPath = 
            if dataDirectory = "" then  config.ResolutionFolder
            elif Path.IsPathRooted dataDirectory then dataDirectory
            else Path.Combine (config.ResolutionFolder, dataDirectory)

        AppDomain.CurrentDomain.SetData("DataDirectory", dataDirectoryFullPath)

        let conn = new SqlConnection(designTimeConnectionString.Value)
        use closeConn = conn.UseLocally()
        conn.CheckVersion()
        conn.LoadDataTypesMap()

        let databaseRootType = ProvidedTypeDefinition(assembly, nameSpace, typeName, baseType = Some typeof<obj>, HideObjectMethods = true)

        let tagProvidedType(t: ProvidedTypeDefinition) =
            t.AddMember(ProvidedProperty("ConnectionStringOrName", typeof<string>, [], IsStatic = true, GetterCode = fun _ -> <@@ connectionStringOrName @@>))

        let schemas = 
            conn.GetUserSchemas() 
            |> List.map (fun schema -> ProvidedTypeDefinition(schema, baseType = Some typeof<obj>, HideObjectMethods = true))
        
        databaseRootType.AddMembers schemas

        let udttsPerSchema = Dictionary()

        for schemaType in schemas do
            let udttsRoot = ProvidedTypeDefinition("User-Defined Table Types", Some typeof<obj>)
            udttsRoot.AddMembersDelayed <| fun () -> 
                this.UDTTs (conn.ConnectionString, schemaType.Name)

            udttsPerSchema.Add( schemaType.Name, udttsRoot)
            schemaType.AddMember udttsRoot
                
        for schemaType in schemas do

            schemaType.AddMembersDelayed <| fun() -> 
                [
                    let routines = this.Routines(conn, schemaType.Name, udttsPerSchema, ResultType.Records, designTimeConnectionString, useReturnValue)
                    routines |> List.iter tagProvidedType
                    yield! routines

                    yield this.Tables(conn, schemaType.Name, designTimeConnectionString, tagProvidedType)
                ]

        let commands = ProvidedTypeDefinition( "Commands", None)
        databaseRootType.AddMember commands
        this.AddCreateCommandMethod(conn, designTimeConnectionString, databaseRootType, udttsPerSchema, commands, connectionStringOrName)

        databaseRootType           

     member internal __.UDTTs( connStr, schema) = [
        for t in getTypes( connStr) do
            if t.TableType && t.Schema = schema
            then 
                yield DesignTime.CreateUDTT( t)
                //tagProvidedType rowType
    ]

    member internal __.Routines(conn, schema, uddtsPerSchema, resultType, designTimeConnectionString, useReturnValue) = 
        [
            use _ = conn.UseLocally()
            let isSqlAzure = conn.IsSqlAzure
            let routines = conn.GetRoutines( schema, isSqlAzure) 
            for routine in routines do
             
                let cmdProvidedType = ProvidedTypeDefinition(snd routine.TwoPartName, Some typeof<``ISqlCommand Implementation``>, HideObjectMethods = true)

                do
                    routine.Description |> Option.iter cmdProvidedType.AddXmlDoc
                
                cmdProvidedType.AddMembersDelayed <| fun() ->
                    [
                        use __ = conn.UseLocally()
                        let parameters = conn.GetParameters( routine, isSqlAzure, useReturnValue)

                        let commandText = routine.ToCommantText(parameters)
                        let outputColumns = DesignTime.GetOutputColumns(conn, commandText, parameters, routine.IsStoredProc)
                        let rank = if routine.Type = ScalarValuedFunction then ResultRank.ScalarValue else ResultRank.Sequence

                        let hasOutputParameters = parameters |> List.exists (fun x -> x.Direction.HasFlag( ParameterDirection.Output))

                        let output = DesignTime.GetOutputTypes(outputColumns, resultType, rank, hasOutputParameters)
        
                        do  //Record
                            output.ProvidedRowType |> Option.iter cmdProvidedType.AddMember

                        //ctors
                        let sqlParameters = Expr.NewArray( typeof<SqlParameter>, parameters |> List.map QuotationsFactory.ToSqlParam)

                        let designTimeConfig = 
                            let expectedDataReaderColumns = 
                                Expr.NewArray(
                                    typeof<string * string>, 
                                    [ for c in outputColumns -> Expr.NewTuple [ Expr.Value c.Name; Expr.Value c.TypeInfo.ClrTypeFullName ] ]
                                )

                            <@@ {
                                SqlStatement = commandText
                                IsStoredProcedure = %%Expr.Value( routine.IsStoredProc)
                                Parameters = %%sqlParameters
                                ResultType = resultType
                                Rank = rank
                                RowMapping = %%output.RowMapping
                                ItemTypeName = %%Expr.Value( output.ErasedToRowType.PartialAssemblyQualifiedName)
                                ExpectedDataReaderColumns = %%expectedDataReaderColumns
                            } @@>

                        yield! DesignTime.GetCommandCtors(
                            cmdProvidedType, 
                            designTimeConfig, 
                            designTimeConnectionString,
                            config.IsHostedExecution
                        )

                        let executeArgs = DesignTime.GetExecuteArgs(cmdProvidedType, parameters, uddtsPerSchema)

                        yield upcast DesignTime.AddGeneratedMethod(parameters, hasOutputParameters, executeArgs, cmdProvidedType.BaseType, output.ProvidedType, "Execute") 

                        if not hasOutputParameters
                        then                              
                            let asyncReturnType = ProvidedTypeBuilder.MakeGenericType(typedefof<_ Async>, [ output.ProvidedType ])
                            yield upcast DesignTime.AddGeneratedMethod(parameters, hasOutputParameters, executeArgs, cmdProvidedType.BaseType, asyncReturnType, "AsyncExecute")

                        if output.ErasedToRowType <> typeof<Void>
                        then 
                            let providedReturnType = 
                                ProvidedTypeBuilder.MakeGenericType(
                                    typedefof<_ option>, 
                                    [ (match output.ProvidedRowType with None -> output.ErasedToRowType | Some x -> upcast x)  ]
                                ) 

                            let providedAsyncReturnType = 
                                ProvidedTypeBuilder.MakeGenericType(
                                    typedefof<_ Async>, 
                                    [ providedReturnType ]
                                ) 

                            if not hasOutputParameters
                            then                              
                                yield upcast DesignTime.AddGeneratedMethod(parameters, hasOutputParameters, executeArgs, cmdProvidedType.BaseType, providedReturnType, "ExecuteSingle") 
                                yield upcast DesignTime.AddGeneratedMethod(parameters, hasOutputParameters, executeArgs, cmdProvidedType.BaseType, providedAsyncReturnType, "AsyncExecuteSingle")
                    ]

                yield cmdProvidedType
        ]

    member internal __.Tables(conn: SqlConnection, schema, connectionString, tagProvidedType) = 
        let tables = ProvidedTypeDefinition("Tables", Some typeof<obj>)
        //tagProvidedType tables
        tables.AddMembersDelayed <| fun() ->
            use __ = conn.UseLocally()
            let isSqlAzure = conn.IsSqlAzure
            conn.GetTables(schema, isSqlAzure)
            |> List.map (fun (tableName, baseTableName, baseSchemaName, description) -> 

                let twoPartTableName = sprintf "[%s].[%s]" schema tableName 

                let descriptionSelector = 
                    if isSqlAzure 
                    then "(SELECT NULL AS Value)"
                    else "fn_listextendedproperty ('MS_Description', 'schema', @schema, 'table', @tableName, 'column', columns.name)"

                let query = 
                    sprintf "
                        SELECT 
	                        columns.name
	                        ,columns.system_type_id
	                        ,columns.user_type_id
	                        ,columns.max_length
	                        ,columns.is_nullable
	                        ,is_identity AS is_identity_column
	                        ,is_updateable = CONVERT(BIT, CASE WHEN is_identity = 0 AND is_computed = 0 THEN 1 ELSE 0 END) 
	                        ,is_part_of_unique_key = CONVERT(BIT, CASE WHEN index_columns.object_id IS NULL THEN 0 ELSE 1 END)
	                        ,default_constraint = ISNULL(OBJECT_DEFINITION(default_object_id), '')
	                        ,description = ISNULL(XProp.Value, '')
                        FROM 
	                        sys.schemas 
	                        JOIN sys.tables ON 
		                        tables.schema_id = schemas.schema_id 
		                        AND schemas.name = @schema 
		                        AND tables.name = @tableName
	                        JOIN sys.columns ON columns.object_id = tables.object_id
	                        LEFT JOIN sys.indexes ON 
		                        tables.object_id = indexes.object_id 
		                        AND indexes.is_primary_key = 1
	                        LEFT JOIN sys.index_columns ON 
		                        index_columns.object_id = tables.object_id 
		                        AND index_columns.index_id = indexes.index_id 
		                        AND columns.column_id = index_columns.column_id
                            OUTER APPLY %s AS XProp
                        ORDER BY 
                            columns.column_id
                        "  descriptionSelector

                let cmd = new SqlCommand(query, conn)
                cmd.Parameters.AddWithValue("@tableName", baseTableName) |> ignore
                cmd.Parameters.AddWithValue("@schema", baseSchemaName) |> ignore

                let columns =  
                    cmd.ExecuteQuery( fun x ->
                        let c = 
                            Column.Parse(
                                x, 
                                (fun(system_type_id, user_type_id) -> findTypeInfoBySqlEngineTypeId(conn.ConnectionString, system_type_id, user_type_id)),
                                ?defaultValue = x.TryGetValue("default_constraint"), 
                                ?description = x.TryGetValue("description")
                            ) 
                        if c.DefaultConstraint <> "" && c.PartOfUniqueKey 
                        then 
                            { c with PartOfUniqueKey = false } 
                            //ADO.NET doesn't allow nullable columns as part of primary key
                            //remove from PK if default value provided by DB on insert.
                        else c
                    )
                    |> Seq.toList
                

                //type data row
                let dataRowType = ProvidedTypeDefinition("Row", Some typeof<DataRow>)
                do 
                    for c in columns do
                        let property = 
                            let name, dataType = c.Name, c.TypeInfo.ClrType
                            if c.Nullable 
                            then
                                let propertType = typedefof<_ option>.MakeGenericType dataType
                                let property = ProvidedProperty(name, propertType, GetterCode = QuotationsFactory.GetBody("GetNullableValueFromDataRow", dataType, name))
                                
                                if not c.ReadOnly
                                then 
                                    property.SetterCode <- QuotationsFactory.GetBody("SetNullableValueInDataRow", dataType, name)
                                
                                property
                            else
                                let property = ProvidedProperty(name, dataType)
                                property.GetterCode <- 
                                    if c.Identity && c.TypeInfo.ClrType <> typeof<int>
                                    then
                                        fun args -> 
                                            <@@ 
                                                let value = (%%args.[0] : DataRow).[name]
                                                let targetType = Type.GetType(%%Expr.Value( c.TypeInfo.ClrTypeFullName), throwOnError = true)
                                                Convert.ChangeType(value, targetType)
                                            @@>
                                    else
                                        fun args -> <@@ (%%args.[0] : DataRow).[name] @@>
                                
                                if not c.ReadOnly
                                then 
                                    property.SetterCode <- fun args -> <@@ (%%args.[0] : DataRow).[name] <- %%Expr.Coerce(args.[1], typeof<obj>) @@>
                                
                                property

                        if c.Description <> "" 
                        then property.AddXmlDoc c.Description

                        dataRowType.AddMember property

                //type data table
                let dataTableType = DesignTime.GetDataTableType tableName dataRowType columns
                DesignTime.SetupTableProperty dataRowType dataTableType
                tagProvidedType dataTableType
                dataTableType.AddMember dataRowType
        
                do
                    description |> Option.iter (fun x -> dataTableType.AddXmlDoc( sprintf "<summary>%s</summary>" x))

                do //ctor
                    let ctor = ProvidedConstructor []
                    ctor.InvokeCode <- fun _ -> 
                        let serializedSchema = 
                            columns 
                            |> List.map (fun x ->
                                let nullable = x.Nullable || x.HasDefaultConstraint
                                sprintf "%s\t%s\t%b\t%i\t%b\t%b\t%b" 
                                    x.Name x.TypeInfo.ClrTypeFullName nullable x.MaxLength x.ReadOnly x.Identity x.PartOfUniqueKey                                 
                            ) 
                            |> String.concat "\n"

                        <@@ 
                            
                            let connectionString = lazy %%connectionString.RunTimeValueExpr(config.IsHostedExecution)
                            let selectCommand = new SqlCommand("SELECT * FROM " + twoPartTableName)
                            let table = new DataTable<DataRow>(selectCommand, connectionString)
                            table.TableName <- twoPartTableName

                            let primaryKey = ResizeArray()
                            for line in serializedSchema.Split('\n') do
                                let xs = line.Split('\t')
                                let col = new DataColumn()
                                col.ColumnName <- xs.[0]
                                col.DataType <- Type.GetType( xs.[1], throwOnError = true)  
                                col.AllowDBNull <- Boolean.Parse xs.[2]
                                if col.DataType = typeof<string>
                                then 
                                    col.MaxLength <- int xs.[3]
                                col.ReadOnly <- Boolean.Parse xs.[4]
                                col.AutoIncrement <- Boolean.Parse xs.[5]
                                if Boolean.Parse xs.[6]
                                then    
                                    primaryKey.Add col 
                                table.Columns.Add col

                            table.PrimaryKey <- Array.ofSeq primaryKey

                            table
                        @@>
                    dataTableType.AddMember ctor
                
                do
                    let parameters, updateableColumns = 
                        [ 
                            for c in columns do 
                                if not(c.Identity || c.ReadOnly)
                                then 
                                    let dataType = c.TypeInfo.ClrType
                                    let parameter = 
                                        if c.NullableParameter
                                        then ProvidedParameter(c.Name, parameterType = typedefof<_ option>.MakeGenericType dataType, optionalValue = null)
                                        else ProvidedParameter(c.Name, dataType)

                                    yield parameter, c
                        ] 
                        |> List.sortBy (fun (_, c) -> c.NullableParameter) //move non-nullable params in front
                        |> List.unzip


                    let methodXmlDoc = 
                        String.concat "\n" [
                            for c in updateableColumns do
                                if c.Description <> "" 
                                then 
                                    let defaultConstrain = 
                                        if c.HasDefaultConstraint 
                                        then sprintf " Default constraint: %s." c.DefaultConstraint
                                        else ""
                                    yield sprintf "<param name='%s'>%s%s</param>" c.Name c.Description defaultConstrain
                        ]
                        
                    let invokeCode = fun (args: _ list)-> 

                        let argsValuesConverted = 
                            (args.Tail, updateableColumns)
                            ||> List.map2 (fun valueExpr c ->
                                if c.NullableParameter
                                then 
                                    typeof<QuotationsFactory>
                                        .GetMethod("OptionToObj", BindingFlags.NonPublic ||| BindingFlags.Static)
                                        .MakeGenericMethod(c.TypeInfo.ClrType)
                                        .Invoke(null, [| box valueExpr |])
                                        |> unbox
                                else
                                    valueExpr
                            )

                        <@@ 
                            let table: DataTable<DataRow> = %%args.[0]
                            let row = table.NewRow()

                            let values: obj[] = %%Expr.NewArray(typeof<obj>, [ for x in argsValuesConverted -> Expr.Coerce(x, typeof<obj>) ])
                            let namesOfUpdateableColumns: string[] = %%Expr.NewArray(typeof<string>, [ for c in updateableColumns -> Expr.Value(c.Name) ])
                            let optionalParams: bool[] = %%Expr.NewArray(typeof<bool>, [ for c in updateableColumns -> Expr.Value(c.NullableParameter) ])

                            Debug.Assert(values.Length = namesOfUpdateableColumns.Length, "values.Length = namesOfUpdateableColumns.Length")
                            Debug.Assert(values.Length = optionalParams.Length, "values.Length = optionalParams.Length")

                            for name, value, optional in Array.zip3 namesOfUpdateableColumns values optionalParams do 
                                row.[name] <- if value = null && optional then box DbNull else value
                            row
                        @@>

                    do 
                        let newRowMethod = ProvidedMethod("NewRow", parameters, dataRowType, InvokeCode = invokeCode)
                        if methodXmlDoc <> "" then newRowMethod.AddXmlDoc methodXmlDoc
                        dataTableType.AddMember newRowMethod

                        let addRowMethod = ProvidedMethod("AddRow", parameters, typeof<Void>)
                        if methodXmlDoc <> "" then addRowMethod.AddXmlDoc methodXmlDoc
                        addRowMethod.InvokeCode <- fun args -> 
                            let newRow = invokeCode args
                            <@@
                                let table: DataTable<DataRow> = %%args.[0]
                                let row: DataRow = %%newRow
                                table.Rows.Add row
                            @@>
                        dataTableType.AddMember addRowMethod

                do //columns accessors
                    for c in columns do
                        let name = c.Name

                        let columnProperty = 
                            ProvidedProperty(
                                name + "Column"
                                , typeof<DataColumn>
                                , []
                                , GetterCode = fun args -> <@@ (%%Expr.Coerce(args.[0], typeof<DataTable>): DataTable).Columns.[name]  @@>
                                )
                        columnProperty.AddObsoleteAttribute(sprintf "This property is deprecated, please use Columns.%s instead." name)
                        dataTableType.AddMember columnProperty

                dataTableType
            )
        tables
        
    member internal this.AddCreateCommandMethod(conn, designTimeConnectionString, rootType: ProvidedTypeDefinition, udttsPerSchema, commands: ProvidedTypeDefinition, tag) = 
        let staticParams = [
            ProvidedStaticParameter("CommandText", typeof<string>) 
            ProvidedStaticParameter("ResultType", typeof<ResultType>, ResultType.Records) 
            ProvidedStaticParameter("SingleRow", typeof<bool>, false)   
            ProvidedStaticParameter("AllParametersOptional", typeof<bool>, false) 
            ProvidedStaticParameter("TypeName", typeof<string>, "") 
        ]
        let m = ProvidedMethod("CreateCommand", [], typeof<obj>, IsStaticMethod = true)
        m.DefineStaticParameters(staticParams, (fun methodName args ->

            let getMethodImpl = 
                lazy 
                    let sqlStatement, resultType, singleRow, allParametersOptional, typename = 
                        unbox args.[0], unbox args.[1], unbox args.[2], unbox args.[3], unbox args.[4]
            
                    if singleRow && not (resultType = ResultType.Records || resultType = ResultType.Tuples)
                    then 
                        invalidArg "singleRow" "singleRow can be set only for ResultType.Records or ResultType.Tuples."

                    use __ = conn.UseLocally()
                    let parameters = DesignTime.ExtractParameters(conn, sqlStatement, allParametersOptional)

                    let outputColumns = 
                        if resultType <> ResultType.DataReader
                        then DesignTime.GetOutputColumns(conn, sqlStatement, parameters, isStoredProcedure = false)
                        else []

                    let rank = if singleRow then ResultRank.SingleRow else ResultRank.Sequence
                    let output = DesignTime.GetOutputTypes(outputColumns, resultType, rank, hasOutputParameters = false)

                    let commandTypeName = if typename <> "" then typename else methodName.Replace("=", "").Replace("@", "")
                    let cmdProvidedType = ProvidedTypeDefinition(commandTypeName, Some typeof<``ISqlCommand Implementation``>, HideObjectMethods = true)

                    do  
                        cmdProvidedType.AddMember(ProvidedProperty("ConnectionStringOrName", typeof<string>, [], IsStatic = true, GetterCode = fun _ -> <@@ tag @@>))

                    do  //AsyncExecute, Execute, and ToTraceString

                        let executeArgs = DesignTime.GetExecuteArgs(cmdProvidedType, parameters, udttsPerSchema)

                        let addRedirectToISqlCommandMethod outputType name = 
                            let hasOutputParameters = false
                            DesignTime.AddGeneratedMethod(parameters, hasOutputParameters, executeArgs, cmdProvidedType.BaseType, outputType, name) 
                            |> cmdProvidedType.AddMember

                        addRedirectToISqlCommandMethod output.ProvidedType "Execute" 
                            
                        let asyncReturnType = ProvidedTypeBuilder.MakeGenericType(typedefof<_ Async>, [ output.ProvidedType ])
                        addRedirectToISqlCommandMethod asyncReturnType "AsyncExecute" 

                        addRedirectToISqlCommandMethod typeof<string> "ToTraceString" 

                    commands.AddMember cmdProvidedType
                    if resultType = ResultType.DataTable then
                        // if we don't do this, we get a compile error
                        // Error The type provider 'FSharp.Data.SqlProgrammabilityProvider' reported an error: type 'Table' was not added as a member to a declaring type <type instanciation name> 
                        cmdProvidedType.AddMember( output.ProvidedType) 
                    else
                        output.ProvidedRowType |> Option.iter cmdProvidedType.AddMember

                    let designTimeConfig = 
                        let expectedDataReaderColumns = 
                            Expr.NewArray(
                                typeof<string * string>, 
                                [ for c in outputColumns -> Expr.NewTuple [ Expr.Value c.Name; Expr.Value c.TypeInfo.ClrTypeFullName ] ]
                            )

                        <@@ {
                            SqlStatement = sqlStatement
                            IsStoredProcedure = false
                            Parameters = %%Expr.NewArray( typeof<SqlParameter>, parameters |> List.map QuotationsFactory.ToSqlParam)
                            ResultType = resultType
                            Rank = rank
                            RowMapping = %%output.RowMapping
                            ItemTypeName = %%Expr.Value( output.ErasedToRowType.PartialAssemblyQualifiedName)
                            ExpectedDataReaderColumns = %%expectedDataReaderColumns
                        } @@>


                    let ctorsAndFactories = 
                        DesignTime.GetCommandCtors(
                            cmdProvidedType, 
                            designTimeConfig, 
                            designTimeConnectionString,
                            config.IsHostedExecution,
                            factoryMethodName = methodName
                        )
                    assert (ctorsAndFactories.Length = 4)
                    let impl: ProvidedMethod = downcast ctorsAndFactories.[3] 
                    rootType.AddMember impl
                    impl

            methodsCache.GetOrAdd(methodName, getMethodImpl)
        ))
        rootType.AddMember m

