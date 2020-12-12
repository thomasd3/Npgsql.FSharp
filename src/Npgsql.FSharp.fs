namespace Npgsql.FSharp

open System
open Npgsql
open NpgsqlTypes
open System.Threading
open System.Data
open System.Security.Cryptography.X509Certificates

/// Specifies how to manage SSL.
[<RequireQualifiedAccess>]
type SslMode =
    /// SSL is disabled. If the server requires SSL, the connection will fail.
    | Disable
    /// Prefer SSL connections if the server allows them, but allow connections without SSL.
    | Prefer
    /// Fail the connection if the server doesn't support SSL.
    | Require
    with
      member this.Serialize() =
        match this with
        | Disable -> "Disable"
        | Prefer -> "Prefer"
        | Require -> "Require"

[<RequireQualifiedAccess>]
module Sql =
    type ExecutionTarget =
        | ConnectionString of string
        | Connection of NpgsqlConnection
        | Transaction of NpgsqlTransaction
        | Empty

    type ConnectionStringBuilder = private {
        Host: string
        Database: string
        Username: string option
        Password: string option
        Port: int option
        Config : string option
        SslMode : SslMode option
        TrustServerCertificate : bool option
        ConvertInfinityDateTime : bool option
    }

    type SqlProps = private {
        ExecutionTarget : ExecutionTarget
        SqlQuery : string list
        Parameters : (string * SqlValue) list
        IsFunction : bool
        NeedPrepare : bool
        CancellationToken: CancellationToken
        ClientCertificate: X509Certificate option
    }

    let private defaultConString() : ConnectionStringBuilder = {
        Host = ""
        Database = ""
        Username = None
        Password = None
        Port = Some 5432
        Config = None
        SslMode = None
        TrustServerCertificate = None
        ConvertInfinityDateTime = None
    }

    let private defaultProps() = {
        ExecutionTarget = Empty
        SqlQuery = [];
        Parameters = [];
        IsFunction = false
        NeedPrepare = false
        ClientCertificate = None
        CancellationToken = CancellationToken.None
    }

    let connect (constr: string) =
        if Uri.IsWellFormedUriString(constr, UriKind.Absolute) && constr.StartsWith "postgres://"
        then { defaultProps() with ExecutionTarget = ConnectionString (Uri(constr).ToPostgresConnectionString()) }
        else { defaultProps() with ExecutionTarget = ConnectionString (constr) }

    let clientCertificate cert props = { props with ClientCertificate = Some cert }
    let host x = { defaultConString() with Host = x }
    let username username config = { config with Username = Some username }
    /// Specifies the password of the user that is logging in into the database server
    let password password config = { config with Password = Some password }
    /// Specifies the database name
    let database x con = { con with Database = x }
    /// Specifies how to manage SSL Mode.
    let sslMode mode config = { config with SslMode = Some mode }
    let requireSslMode config = { config with SslMode = Some SslMode.Require }

    let cancellationToken token config = { config with CancellationToken = token }
    /// Specifies the port of the database server. If you don't specify the port, the default port of `5432` is used.
    let port port config = { config with Port = Some port }
    let trustServerCertificate value config = { config with TrustServerCertificate = Some value }
    let convertInfinityDateTime value config = { config with ConvertInfinityDateTime = Some value }
    let config extraConfig config = { config with Config = Some extraConfig }
    let formatConnectionString (config:ConnectionStringBuilder) =
        [
            Some (sprintf "Host=%s" config.Host)
            config.Port |> Option.map (sprintf "Port=%d")
            Some (sprintf "Database=%s" config.Database)
            config.Username |> Option.map (sprintf "Username=%s")
            config.Password |> Option.map (sprintf "Password=%s")
            config.SslMode |> Option.map (fun mode -> sprintf "SslMode=%s" (mode.Serialize()))
            config.TrustServerCertificate |> Option.map (sprintf "Trust Server Certificate=%b")
            config.ConvertInfinityDateTime |> Option.map (sprintf "Convert Infinity DateTime=%b")
            config.Config
        ]
        |> List.choose id
        |> String.concat ";"

    let connectFromConfig (connectionConfig: ConnectionStringBuilder) =
        connect (formatConnectionString connectionConfig)

    /// Turns the given postgres Uri into a proper connection string
    let fromUri (uri: Uri) = uri.ToPostgresConnectionString()
    /// Creates initial database connection configration from a the Uri components.
    /// It try to find `Host`, `Username`, `Password`, `Database` and `Port` from the input `Uri`.
    let fromUriToConfig (uri: Uri) =
        let extractHost (uri: Uri) =
            if String.IsNullOrWhiteSpace uri.Host
            then Some ("Host", "localhost")
            else Some ("Host", uri.Host)

        let extractUser (uri: Uri) =
            if uri.UserInfo.Contains ":" then
                match uri.UserInfo.Split ':' with
                | [| username; password|] ->
                  [ ("Username", username); ("Password", password) ]
                | otherwise -> [ ]
            elif not (String.IsNullOrWhiteSpace uri.UserInfo) then
                ["Username", uri.UserInfo ]
            else
                [ ]

        let extractDatabase (uri: Uri) =
            match uri.LocalPath.Split '/' with
            | [| ""; databaseName |] -> Some ("Database", databaseName)
            | otherwise -> None

        let extractPort (uri: Uri) =
            match uri.Port with
            | -1 -> Some ("Port", "5432")
            | n -> Some ("Port", string n)

        let uriParts =
            [ extractHost uri; extractDatabase uri; extractPort uri ]
            |> List.choose id
            |> List.append (extractUser uri)

        let updateConfig config (partName, value) =
            match partName with
            | "Host" -> { config with Host = value }
            | "Username" -> { config with Username = Some value }
            | "Password" -> { config with Password = Some value }
            | "Database" -> { config with Database = value }
            | "Port" -> { config with Port = Some (int value) }
            | otherwise -> config

        (defaultConString(), uriParts)
        ||> List.fold updateConfig

    /// Uses an existing connection to execute SQL commands against
    let existingConnection (connection: NpgsqlConnection) = { defaultProps() with ExecutionTarget = Connection connection }
    /// Uses an existing transaction to execute the SQL commands against
    let transaction (existingTransaction: NpgsqlTransaction) =
        { defaultProps() with ExecutionTarget = Transaction existingTransaction }

    /// Configures the SQL query to execute
    let query (sql: string) props = { props with SqlQuery = [sql] }
    let func (sql: string) props = { props with SqlQuery = [sql]; IsFunction = true }
    let prepare  props = { props with NeedPrepare = true }
    /// Provides the SQL parameters for the query
    let parameters ls props = { props with Parameters = ls }
    /// When using the Npgsql.FSharp.Analyzer, this function annotates the code to tell the analyzer to ignore and skip the SQL analyzer against the database.
    let skipAnalysis (props: SqlProps) = props
    /// Creates or returns the SQL connection used to execute the SQL commands
    let createConnection (props: SqlProps): NpgsqlConnection =
        match props.ExecutionTarget with
        | ConnectionString connectionString ->
            let connection = new NpgsqlConnection(connectionString)
            match props.ClientCertificate with
            | Some cert ->
                connection.ProvideClientCertificatesCallback <- new ProvideClientCertificatesCallback(fun certs ->
                    certs.Add(cert) |> ignore)
            | None -> ()
            connection

        | Connection existingConnection -> existingConnection
        | Transaction transaction -> transaction.Connection
        | Empty -> failwith "Could not create a connection from empty parameters."

    let private makeCommand (props: SqlProps) (connection: NpgsqlConnection) =
        match props.ExecutionTarget with
        | ConnectionString _
        | Connection _ -> new NpgsqlCommand(List.head props.SqlQuery, connection)
        | Transaction transaction -> new NpgsqlCommand(List.head props.SqlQuery, connection, transaction)
        | Empty -> failwith "Cannot create command from an empty execution target"

    let private populateRow (cmd: NpgsqlCommand) (row: (string * SqlValue) list) =
        for (paramName, value) in row do

            let normalizedParameterName =
                let paramName = paramName.Trim()
                if not (paramName.StartsWith "@")
                then sprintf "@%s" paramName
                else paramName

            let add value valueType =
                cmd.Parameters.AddWithValue(normalizedParameterName, valueType, value)
                |> ignore

            match value with
            | SqlValue.Bit bit -> add bit NpgsqlDbType.Bit
            | SqlValue.String text -> add text NpgsqlDbType.Text
            | SqlValue.Int number -> add number NpgsqlDbType.Integer
            | SqlValue.Uuid uuid -> add uuid NpgsqlDbType.Uuid
            | SqlValue.UuidArray uuidArray -> add uuidArray (NpgsqlDbType.Array ||| NpgsqlDbType.Uuid)
            | SqlValue.Short number -> add number NpgsqlDbType.Smallint
            | SqlValue.Date date -> add date NpgsqlDbType.Date
            | SqlValue.Timestamp timestamp -> add timestamp NpgsqlDbType.Timestamp
            | SqlValue.TimestampWithTimeZone timestampTz -> add timestampTz NpgsqlDbType.TimestampTz
            | SqlValue.Number number -> add number NpgsqlDbType.Double
            | SqlValue.Bool boolean -> add boolean NpgsqlDbType.Boolean
            | SqlValue.Decimal number -> add number NpgsqlDbType.Numeric
            | SqlValue.Money number -> add number NpgsqlDbType.Money
            | SqlValue.Long number -> add number NpgsqlDbType.Bigint
            | SqlValue.Bytea binary -> add binary NpgsqlDbType.Bytea
            | SqlValue.TimeWithTimeZone x -> add x NpgsqlDbType.TimeTz
            | SqlValue.Null -> cmd.Parameters.AddWithValue(normalizedParameterName, DBNull.Value) |> ignore
            | SqlValue.TinyInt x -> cmd.Parameters.AddWithValue(normalizedParameterName, x) |> ignore
            | SqlValue.Jsonb x -> add x NpgsqlDbType.Jsonb
            | SqlValue.Time x -> add x NpgsqlDbType.Time
            | SqlValue.StringArray x -> add x (NpgsqlDbType.Array ||| NpgsqlDbType.Text)
            | SqlValue.IntArray x -> add x (NpgsqlDbType.Array ||| NpgsqlDbType.Integer)
            | SqlValue.Parameter x ->
                x.ParameterName <- normalizedParameterName
                ignore (cmd.Parameters.Add(x))
            | SqlValue.Point x -> add x NpgsqlDbType.Point

    let private populateCmd (cmd: NpgsqlCommand) (props: SqlProps) =
        if props.IsFunction then cmd.CommandType <- CommandType.StoredProcedure
        populateRow cmd props.Parameters

    let executeTransaction queries (props: SqlProps) =
        try
            if List.isEmpty queries
            then Ok [ ]
            else
            let connection = createConnection props
            try
                if not (connection.State.HasFlag ConnectionState.Open)
                then connection.Open()
                use transaction = connection.BeginTransaction()
                let affectedRowsByQuery = ResizeArray<int>()
                for (query, parameterSets) in queries do
                    if List.isEmpty parameterSets
                    then
                        use command = new NpgsqlCommand(query, connection, transaction)
                        // detect whether the command has parameters
                        // if that is the case, then don't execute it
                        NpgsqlCommandBuilder.DeriveParameters(command)
                        if command.Parameters.Count = 0 then
                            let affectedRows = command.ExecuteNonQuery()
                            affectedRowsByQuery.Add affectedRows
                        else
                            // parameterized query won't execute
                            // when the parameter set is empty
                            affectedRowsByQuery.Add 0
                    else
                      for parameterSet in parameterSets do
                        use command = new NpgsqlCommand(query, connection, transaction)
                        populateRow command parameterSet
                        let affectedRows = command.ExecuteNonQuery()
                        affectedRowsByQuery.Add affectedRows

                transaction.Commit()
                Ok (List.ofSeq affectedRowsByQuery)
            finally
                match props.ExecutionTarget with
                | ConnectionString _ -> connection.Dispose()
                | _ ->
                    // leave connections open
                    // when provided from outside
                    ()

        with
        | error -> Error error

    let executeTransactionAsync queries (props: SqlProps) =
        async {
            try
                let! token = Async.CancellationToken
                use mergedTokenSource = CancellationTokenSource.CreateLinkedTokenSource(token, props.CancellationToken)
                let mergedToken = mergedTokenSource.Token
                if List.isEmpty queries
                then return Ok [ ]
                else
                let connection = createConnection props
                try
                    if not (connection.State.HasFlag ConnectionState.Open)
                    then do! Async.AwaitTask (connection.OpenAsync mergedToken)
                    use transaction = connection.BeginTransaction ()
                    let affectedRowsByQuery = ResizeArray<int>()
                    for (query, parameterSets) in queries do
                        if List.isEmpty parameterSets
                        then
                          use command = new NpgsqlCommand(query, connection, transaction)
                          // detect whether the command has parameters
                          // if that is the case, then don't execute it
                          NpgsqlCommandBuilder.DeriveParameters(command)
                          if command.Parameters.Count = 0 then
                            let! affectedRows = Async.AwaitTask (command.ExecuteNonQueryAsync mergedToken)
                            affectedRowsByQuery.Add affectedRows
                          else
                            // parameterized query won't execute
                            // when the parameter set is empty
                            affectedRowsByQuery.Add 0
                        else
                          for parameterSet in parameterSets do
                            use command = new NpgsqlCommand(query, connection, transaction)
                            populateRow command parameterSet
                            let! affectedRows = Async.AwaitTask (command.ExecuteNonQueryAsync mergedToken)
                            affectedRowsByQuery.Add affectedRows
                    do! Async.AwaitTask(transaction.CommitAsync mergedToken)
                    return Ok (List.ofSeq affectedRowsByQuery)
                finally
                    match props.ExecutionTarget with
                    | ConnectionString _ -> connection.Dispose()
                    | _ -> ()
            with error ->
                return Error error
        }

    let execute (read: RowReader -> 't) (props: SqlProps) : Result<'t list, exn> =
        try
            if List.isEmpty props.SqlQuery
            then raise <| MissingQueryException "No query provided to execute. Please use Sql.query"
            let connection = createConnection props
            try
                if not (connection.State.HasFlag ConnectionState.Open)
                then connection.Open()
                use command = makeCommand props connection
                do populateCmd command props
                if props.NeedPrepare then command.Prepare()
                use reader = command.ExecuteReader()
                let postgresReader = unbox<NpgsqlDataReader> reader
                let rowReader = RowReader(postgresReader)
                let result = ResizeArray<'t>()
                while reader.Read() do result.Add (read rowReader)
                Ok (List.ofSeq result)
            finally
                match props.ExecutionTarget with
                | ConnectionString _ -> connection.Dispose()
                | _ -> ()
        with error ->
            Error error

    let iter (perform: RowReader -> unit) (props: SqlProps) : Result<unit, exn> =
        try
            if List.isEmpty props.SqlQuery
            then raise <| MissingQueryException "No query provided to execute. Please use Sql.query"
            let connection = createConnection props
            try
                if not (connection.State.HasFlag ConnectionState.Open)
                then connection.Open()
                use command = makeCommand props connection
                do populateCmd command props
                if props.NeedPrepare then command.Prepare()
                use reader = command.ExecuteReader()
                let postgresReader = unbox<NpgsqlDataReader> reader
                let rowReader = RowReader(postgresReader)
                while reader.Read() do perform rowReader
                Ok ()
            finally
                match props.ExecutionTarget with
                | ConnectionString _ -> connection.Dispose()
                | _ -> ()
        with error ->
            Error error

    let executeRow (read: RowReader -> 't) (props: SqlProps) : Result<'t, exn> =
        try
            if List.isEmpty props.SqlQuery
            then raise <| MissingQueryException "No query provided to execute. Please use Sql.query"
            let connection = createConnection props
            try
                if not (connection.State.HasFlag ConnectionState.Open)
                then connection.Open()
                use command = makeCommand props connection
                do populateCmd command props
                if props.NeedPrepare then command.Prepare()
                use reader = command.ExecuteReader()
                let postgresReader = unbox<NpgsqlDataReader> reader
                let rowReader = RowReader(postgresReader)
                if reader.Read()
                then Ok (read rowReader)
                else raise <| NoResultsException "Expected at least one row to be returned from the result set. Instead it was empty"
            finally
                match props.ExecutionTarget with
                | ConnectionString _ -> connection.Dispose()
                | _ -> ()
        with error ->
            Error error

    let executeAsync (read: RowReader -> 't) (props: SqlProps) : Async<Result<'t list, exn>> =
        async {
            try
                let! token =  Async.CancellationToken
                use mergedTokenSource = CancellationTokenSource.CreateLinkedTokenSource(token, props.CancellationToken)
                let mergedToken = mergedTokenSource.Token
                if List.isEmpty props.SqlQuery
                then raise <| MissingQueryException "No query provided to execute. Please use Sql.query"
                let connection = createConnection props
                try
                    if not (connection.State.HasFlag ConnectionState.Open)
                    then do! Async.AwaitTask(connection.OpenAsync(mergedToken))
                    use command = makeCommand props connection
                    do populateCmd command props
                    if props.NeedPrepare then command.Prepare()
                    use! reader = Async.AwaitTask (command.ExecuteReaderAsync(mergedToken))
                    let postgresReader = unbox<NpgsqlDataReader> reader
                    let rowReader = RowReader(postgresReader)
                    let result = ResizeArray<'t>()
                    while reader.Read() do result.Add (read rowReader)
                    return Ok (List.ofSeq result)
                finally
                    match props.ExecutionTarget with
                    | ConnectionString _ -> connection.Dispose()
                    | _ -> ()
            with error ->
                return Error error
        }

    let iterAsync (perform: RowReader -> unit) (props: SqlProps) : Async<Result<unit, exn>> =
        async {
            try
                let! token =  Async.CancellationToken
                use mergedTokenSource = CancellationTokenSource.CreateLinkedTokenSource(token, props.CancellationToken)
                let mergedToken = mergedTokenSource.Token
                if List.isEmpty props.SqlQuery
                then raise <| MissingQueryException "No query provided to execute. Please use Sql.query"
                let connection = createConnection props
                try
                    if not (connection.State.HasFlag ConnectionState.Open)
                    then do! Async.AwaitTask(connection.OpenAsync(mergedToken))
                    use command = makeCommand props connection
                    do populateCmd command props
                    if props.NeedPrepare then command.Prepare()
                    use! reader = Async.AwaitTask (command.ExecuteReaderAsync(mergedToken))
                    let postgresReader = unbox<NpgsqlDataReader> reader
                    let rowReader = RowReader(postgresReader)
                    while reader.Read() do perform rowReader
                    return Ok ()
                finally
                    match props.ExecutionTarget with
                    | ConnectionString _ -> connection.Dispose()
                    | _ -> ()
            with error ->
                return Error error
        }

    let executeRowAsync (read: RowReader -> 't) (props: SqlProps) : Async<Result<'t, exn>> =
        async {
            try
                let! token =  Async.CancellationToken
                use mergedTokenSource = CancellationTokenSource.CreateLinkedTokenSource(token, props.CancellationToken)
                let mergedToken = mergedTokenSource.Token
                if List.isEmpty props.SqlQuery
                then raise <| MissingQueryException "No query provided to execute. Please use Sql.query"
                let connection = createConnection props
                try
                    if not (connection.State.HasFlag ConnectionState.Open)
                    then do! Async.AwaitTask(connection.OpenAsync(mergedToken))
                    use command = makeCommand props connection
                    do populateCmd command props
                    if props.NeedPrepare then command.Prepare()
                    use! reader = Async.AwaitTask (command.ExecuteReaderAsync(mergedToken))
                    let postgresReader = unbox<NpgsqlDataReader> reader
                    let rowReader = RowReader(postgresReader)
                    if reader.Read()
                    then return Ok (read rowReader)
                    else return! raise <| NoResultsException "Expected at least one row to be returned from the result set. Instead it was empty"
                finally
                    match props.ExecutionTarget with
                    | ConnectionString _ -> connection.Dispose()
                    | _ -> ()
            with error ->
                return Error error
        }

    /// Executes the query and returns the number of rows affected
    let executeNonQuery (props: SqlProps) : Result<int, exn> =
        try
            if List.isEmpty props.SqlQuery
            then raise <| MissingQueryException "No query provided to execute..."
            let connection = createConnection props
            try
                if not (connection.State.HasFlag ConnectionState.Open)
                then connection.Open()
                use command = makeCommand props connection
                populateCmd command props
                if props.NeedPrepare then command.Prepare()
                Ok (command.ExecuteNonQuery())
            finally
                match props.ExecutionTarget with
                | ConnectionString _ -> connection.Dispose()
                | _ -> ()
        with
            | error -> Error error

    /// Executes the query as asynchronously and returns the number of rows affected
    let executeNonQueryAsync  (props: SqlProps) =
        async {
            try
                let! token = Async.CancellationToken
                use mergedTokenSource = CancellationTokenSource.CreateLinkedTokenSource(token, props.CancellationToken)
                let mergedToken = mergedTokenSource.Token
                if List.isEmpty props.SqlQuery
                then raise <| MissingQueryException "No query provided to execute. Please use Sql.query"
                let connection = createConnection props
                try
                    if not (connection.State.HasFlag ConnectionState.Open)
                    then do! Async.AwaitTask (connection.OpenAsync(mergedToken))
                    use command = makeCommand props connection
                    populateCmd command props
                    if props.NeedPrepare then command.Prepare()
                    let! affectedRows = Async.AwaitTask(command.ExecuteNonQueryAsync(mergedToken))
                    return Ok affectedRows
                finally
                    match props.ExecutionTarget with
                    | ConnectionString _ -> connection.Dispose()
                    | _ -> ()
            with
            | error -> return Error error
        }
