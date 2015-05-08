﻿module SqlServerTest

open NUnit.Framework
open FSharp.QueryProvider
open FSharp.QueryProvider.PreparedQuery
open FSharp.QueryProvider.DataReader
open FSharp.QueryProvider

open Models

type IQueryable = System.Linq.IQueryable
type IQueryable<'T> = System.Linq.IQueryable<'T>
type Expression = System.Linq.Expressions.Expression

let provider = EmptyQueryProvider.EmptyQueryProvider()

let queryable<'T>() = Queryable.Query<'T>(provider, None)

let getExpression (f : IQueryable<'t> -> 'r) = 
    let beforeCount = provider.Expressions |> Seq.length 
    let query =
        try
            let r = f(queryable<'t>()) :> obj
            match r with
            | :? System.Linq.IQueryable as q -> Some q
            | _ -> None
        with 
        | e when(e.Message = "EmptyQueryProvider.Execute not implemented") -> None

    if query.IsSome then
        query.Value.Expression
    else
        let afterCount = provider.Expressions |> Seq.length 
        if afterCount > (beforeCount + 1) then
            let expressionMessage = 
                provider.Expressions 
                    |> Seq.skip(beforeCount)
                    |> Seq.map(fun e -> e.ToString())
                    |> String.concat "\n"
            failwithf "too many expressions fired:\n%s" expressionMessage
        else if afterCount < (beforeCount + 1) then
            failwith "no queriesFired"
        else
            provider.Expressions |> Seq.last

let AreEqualTranslateExpression (translate : Expression -> PreparedStatement<_>) get expectedSql (expectedParameters: list<PreparedParameter<_>>) (expectedResultConstructionInfo : ConstructionInfo) : unit =
        
    let expression = getExpression get

    let sqlQuery = translate expression

    if expectedSql <> sqlQuery.Text then
        printfn "expression: %s" (expression.ToString())
        printfn "query: %s" sqlQuery.Text

    Assert.AreEqual(expectedSql, sqlQuery.Text)

    areSeqEqual sqlQuery.Parameters (expectedParameters |> List.toSeq)

    let ctorEqual =
        let e = expectedResultConstructionInfo
        let a = sqlQuery.ResultConstructionInfo
        match a with
        | None -> false
        | Some a -> 
            if e.ReturnType <> a.ReturnType then
                false
            else
                match e.TypeOrLambda, a.TypeOrLambda with 
                | TypeOrLambdaConstructionInfo.Type et, TypeOrLambdaConstructionInfo.Type at ->
                    e.Type = a.Type &&
                    et.Type = at.Type &&
                    compareSeq et.PropertySets at.PropertySets &&
                    compareSeq et.ConstructorArgs at.ConstructorArgs
                | TypeOrLambdaConstructionInfo.Lambda _, TypeOrLambdaConstructionInfo.Lambda _ ->
                    failwith "dont use this function to check lambda constructors"
                | TypeOrLambdaConstructionInfo.Lambda _, TypeOrLambdaConstructionInfo.Type _ -> false
                | TypeOrLambdaConstructionInfo.Type _, TypeOrLambdaConstructionInfo.Lambda _ -> false

    if not ctorEqual then
        Assert.Fail(sprintf "Expected: \n%A \n\nActual: \n%A" (expectedResultConstructionInfo) (sqlQuery.ResultConstructionInfo))

let AreEqualExpression get = AreEqualTranslateExpression (QueryTranslator.translate QueryTranslator.SqlServer2012 QueryTranslator.SelectQuery None None None) get

let AreEqualDeleteOrSelectExpression get select expectedSql (expectedParameters: list<PreparedParameter<_>>) (expectedResultConstructionInfo) : unit =
    AreEqualExpression get (select + expectedSql) expectedParameters expectedResultConstructionInfo
    let expression = getExpression get
    let deleteQuery = (QueryTranslator.translate QueryTranslator.SqlServer2012 QueryTranslator.DeleteQuery None None None) expression
    Assert.AreEqual("DELETE T " + expectedSql, deleteQuery.Text)
    Assert.AreEqual(None, deleteQuery.ResultConstructionInfo)
    areSeqEqual deleteQuery.Parameters (expectedParameters |> List.toSeq)

let AreEqualExpressionLambdaCtor get expectedSql data =
    let expression = getExpression get
    let sqlQuery = (QueryTranslator.translate QueryTranslator.SqlServer2012 QueryTranslator.SelectQuery None None None) expression
    Assert.AreEqual(expectedSql, sqlQuery.Text)

    let reader = 
        new LocalDataReader.LocalDataReader(data)

    match sqlQuery.ResultConstructionInfo with
    | None -> failwith "Should have ctor"
    | Some ctor -> read reader ctor

//use for test data:
//http://fsprojects.github.io/FSharp.Linq.ComposableQuery/QueryExamples.html
//https://msdn.microsoft.com/en-us/library/vstudio/hh225374.aspx
module QueryGenTest = 

    type Value = {
        Value : string
    }
    type TopValue = {
        SubValue : Value
    }
    type MutableObject() = 
        [<DefaultValue>] val mutable Value : string

    let justPersonSelect i =
        {
            Type = typedefof<Person>
            ConstructorArgs = ([0..3] |> Seq.map(fun v -> Value (i + v)))
            PropertySets = []
        }

    let personSelectType returnType i = 
        {
            ReturnType = returnType
            Type = typedefof<Person>
            TypeOrLambda = TypeOrLambdaConstructionInfo.Type (justPersonSelect i)
        }

    let personSelect = personSelectType Many
    
    let employeeSelect i = 
        let createSome t i =
            Type (createTypeConstructionInfo t ([Value i]) [])
         
        let ctorArgs = [
            createSome (Some(1).GetType()) (0 + i) 
            createSome (Some("").GetType()) (1 + i)
            createSome (Some(1).GetType()) (2 + i)
            createSome (Some(1).GetType()) (3 + i)
            Value (4 + i)
        ]
        {
            ReturnType = Many
            Type = typedefof<Employee>
            TypeOrLambda = TypeOrLambdaConstructionInfo.Type {
                Type = typedefof<Employee>
                ConstructorArgs = ctorArgs
                PropertySets = []
            }
        }
        //TypeOrLambdaConstructionInfo.Type (createTypeConstructionInfo typedefof<Employee> Many ctorArgs [])

    let simpleSelect t i returnType = 
        {
            ReturnType = returnType
            Type = t
            TypeOrLambda = TypeOrLambdaConstructionInfo.Type {
                Type = t
                ConstructorArgs = [Value i] 
                PropertySets = []
            }
        }
    let lambdaSelect t returnType lambda parameters = 
        {
            ReturnType = returnType
            Type = t
            TypeOrLambda = TypeOrLambdaConstructionInfo.Lambda {
                Lambda = lambda
                Parameters = parameters
            }
        }

    let simpleOneSelect t i = 
        {
            ReturnType = Single
            Type = t
            TypeOrLambda = TypeOrLambdaConstructionInfo.Type {
                Type = t
                ConstructorArgs = [Value i] 
                PropertySets = []
            }
        }

    let stringSelect = simpleSelect typedefof<string>
    let intSelect = simpleSelect typedefof<int>
    let boolSelect = simpleSelect typedefof<bool>

    [<Test>]
    let ``select simple``() =
        let q = fun (persons : IQueryable<Person>) -> 
            query {
                for p in persons do
                select p
            }
        
        AreEqualExpression q "SELECT T.PersonId, T.PersonName, T.JobKind, T.VersionNo FROM Person AS T" [] (personSelect 0)

    [<Test>]
    let ``select fun invoke``() =
        let func p = 
            { p with PersonName = p.PersonName + "mod" }
        let q = fun (persons : IQueryable<Person>) -> 
            query {
                for p in persons do
                select(func p)
            }
        
        let persons = AreEqualExpressionLambdaCtor q "SELECT T.PersonId, T.PersonName, T.JobKind, T.VersionNo FROM Person AS T" DataReaderData.persons
        let persons = persons :?> seq<Person>
        areSeqEqual [func Data.johnDoe; func Data.jamesWilson] persons

    [<Test>]
    let ``select fun inline``() =
        let q = fun (persons : IQueryable<Person>) -> 
            query {
                for p in persons do
                select(fun () -> p)
            }
        
        let funcs = AreEqualExpressionLambdaCtor q "SELECT T.PersonId, T.PersonName, T.JobKind, T.VersionNo FROM Person AS T" DataReaderData.persons
        let funcs = funcs :?> seq<unit -> Person>
        let persons = (funcs |> Seq.map(fun f -> f()))
        areSeqEqual [Data.johnDoe; Data.jamesWilson] persons
        
    [<Test>]
    let ``select fun self execute``() =
        let q = fun (persons : IQueryable<Person>) -> 
            query {
                for p in persons do
                select((fun () -> p)())
            }
        
        let persons = AreEqualExpressionLambdaCtor q "SELECT T.PersonId, T.PersonName, T.JobKind, T.VersionNo FROM Person AS T" DataReaderData.persons
        let persons = persons :?> seq<Person>
        areSeqEqual [Data.johnDoe; Data.jamesWilson] persons

    [<Test>]
    let ``select fun two arg invoke``() =
        let func p1 p2 = 
            { p1 with PersonName = p1.PersonName + p2.PersonName }
        let q = fun (persons : IQueryable<Person>) -> 
            query {
                for p in persons do
                select(func p p)
            }
        
        let persons = AreEqualExpressionLambdaCtor q "SELECT T.PersonId, T.PersonName, T.JobKind, T.VersionNo FROM Person AS T" DataReaderData.persons
        let persons = persons :?> seq<Person>
        areSeqEqual [func Data.johnDoe Data.johnDoe; func Data.jamesWilson Data.jamesWilson] persons
        
    [<Test>]
    let ``select fun two local arg invoke``() =
        let func p m = 
            { p with PersonName = p.PersonName + m }
        let m = "mod"
        let q = fun (persons : IQueryable<Person>) -> 
            query {
                for p in persons do
                select(func p m)
            }
        
        let persons = AreEqualExpressionLambdaCtor q "SELECT T.PersonId, T.PersonName, T.JobKind, T.VersionNo FROM Person AS T" DataReaderData.persons
        let persons = persons :?> seq<Person>
        areSeqEqual [func Data.johnDoe m; func Data.jamesWilson m] persons

    [<Test; Ignore("Partial selecting when lambda is applied is not supported yet.")>]
    let ``select fun partial``() =
        let func name = 
            name + "mod"
        let q = fun (persons : IQueryable<Person>) -> 
            query {
                for p in persons do
                select(func p.PersonName)
            }
        
        let persons = AreEqualExpressionLambdaCtor q "SELECT T.PersonName FROM Person AS T" DataReaderData.persons
        let personNames = persons :?> seq<string>
        areSeqEqual [func Data.johnDoe.PersonName; func Data.jamesWilson.PersonName] personNames

    [<Test>]
    let ``select fun partial applied``() =
        let func p m = 
            { p with PersonName = p.PersonName + m }
        let q = fun (persons : IQueryable<Person>) -> 
            query {
                for p in persons do
                select(func p)
            }
        
        let funcs = AreEqualExpressionLambdaCtor q "SELECT T.PersonId, T.PersonName, T.JobKind, T.VersionNo FROM Person AS T" DataReaderData.persons
        let funcs = funcs :?> seq<string -> Person>
        let persons = (funcs |> Seq.map(fun f -> f "mod"))
        areSeqEqual [func Data.johnDoe "mod"; func Data.jamesWilson "mod"] persons

    [<Test>]
    let ``select fun full + partial``() =
        let func p m = 
            { p with PersonName = p.PersonName + m }
        let q = fun (persons : IQueryable<Person>) -> 
            query {
                for p in persons do
                select(func p p.PersonName)
            }
        
        let persons = AreEqualExpressionLambdaCtor q "SELECT T.PersonId, T.PersonName, T.JobKind, T.VersionNo FROM Person AS T" DataReaderData.persons
        let persons = persons :?> seq<Person>
        areSeqEqual [func Data.johnDoe Data.johnDoe.PersonName; func Data.jamesWilson Data.jamesWilson.PersonName] persons

    [<Test>]
    let ``select some``() =
        let q = fun (persons : IQueryable<Person>) -> 
            query {
                for p in persons do
                select(Some p)
            }
        
        let persons = AreEqualExpressionLambdaCtor q "SELECT T.PersonId, T.PersonName, T.JobKind, T.VersionNo FROM Person AS T" DataReaderData.persons
        let persons = persons :?> seq<Person option>
        areSeqEqual [Some Data.johnDoe; Some Data.jamesWilson] persons

    [<Test; Ignore("Partial selecting when lambda is applied is not supported yet.")>]
    let ``select some partial``() =
        let q = fun (persons : IQueryable<Person>) -> 
            query {
                for p in persons do
                select(Some p.PersonName)
            }
        
        let persons = AreEqualExpressionLambdaCtor q "SELECT T.PersonName FROM Person AS T" DataReaderData.persons
        let personNames = persons :?> seq<string option>
        areSeqEqual [Some Data.johnDoe.PersonName; Some Data.jamesWilson.PersonName] personNames

    [<Test>]
    let ``where simple``() =
        let q = fun (persons : IQueryable<Person>) -> 
            query {
                for p in persons do
                where(p.PersonName = "john")
                select p
            }
        
        AreEqualExpression q "SELECT T.PersonId, T.PersonName, T.JobKind, T.VersionNo FROM Person AS T WHERE (T.PersonName = @p1)" [
            {Name="@p1"; Value="john"; DbType = System.Data.SqlDbType.NVarChar}
        ] (personSelect 0)

    [<Test>]
    let ``extend column name``() =
        
        let q = fun (persons : IQueryable<Person>) -> 
            query {
                for p in persons do
                where(p.PersonName = "john")
                where(p.JobKind = JobKind.Manager)
                select p
            }
        
        let translate = 
            QueryTranslator.translate QueryTranslator.SqlServer2012 QueryTranslator.SelectQuery None None (Some (fun m ->
                if m.Name = "PersonName" then
                    Some (m.Name + "Mod")
                else
                    None
            ))
        AreEqualTranslateExpression translate q "SELECT T.PersonId, T.PersonNameMod, T.JobKind, T.VersionNo FROM Person AS T WHERE (T.PersonNameMod = @p1 AND T.JobKind = @p2)" [
            {Name="@p1"; Value=("john"); DbType = System.Data.SqlDbType.NVarChar}
            {Name="@p2"; Value=(JobKind.Manager); DbType = System.Data.SqlDbType.Int}
        ] (personSelect 0)

    [<Test>]
    let ``extend table name``() =
        
        let q = fun (persons : IQueryable<Person>) -> 
            query {
                for p in persons do
                where(query {
                    for e in queryable<Employee>() do
                    select e.PersonId
                    contains p.PersonId
                })
                select p
            }
        
        let translate = 
            QueryTranslator.translate QueryTranslator.SqlServer2012 QueryTranslator.SelectQuery None (Some (fun t ->
                if t.Name = "Person" then
                    Some (t.Name + "Mod")
                else
                    None
            )) None


        AreEqualTranslateExpression translate q "SELECT T.PersonId, T.PersonName, T.JobKind, T.VersionNo FROM PersonMod AS T WHERE (T.PersonId IN (SELECT T2.PersonId FROM Employee AS T2))" [] (personSelect 0)

    [<Test>]
    let ``where local var``() =
        
        let name = ref "john"
        let q = fun (persons : IQueryable<Person>) -> 
            query {
                for p in persons do
                where(p.PersonName = !name)
                select p
            }
        
        AreEqualDeleteOrSelectExpression q "SELECT T.PersonId, T.PersonName, T.JobKind, T.VersionNo " "FROM Person AS T WHERE (T.PersonName = @p1)" [
            {Name="@p1"; Value=(!name); DbType = System.Data.SqlDbType.NVarChar}
        ] (personSelect 0)

    [<Test>]
    let ``where property access``() =
        
        let value = { SubValue = {Value = "john" } }
        let q = fun (persons : IQueryable<Person>) -> 
            query {
                for p in persons do
                where(p.PersonName = value.SubValue.Value)
                select p
            }
        
        AreEqualDeleteOrSelectExpression q "SELECT T.PersonId, T.PersonName, T.JobKind, T.VersionNo " "FROM Person AS T WHERE (T.PersonName = @p1)" [
            {Name="@p1"; Value=("john"); DbType = System.Data.SqlDbType.NVarChar}
        ] (personSelect 0)

    [<Test>]
    let ``where field access``() =
        
        let value = MutableObject()
        value.Value <- "john"
        let q = fun (persons : IQueryable<Person>) -> 
            query {
                for p in persons do
                where(p.PersonName = value.Value)
                select p
            }
        
        AreEqualDeleteOrSelectExpression q "SELECT T.PersonId, T.PersonName, T.JobKind, T.VersionNo " "FROM Person AS T WHERE (T.PersonName = @p1)" [
            {Name="@p1"; Value=("john"); DbType = System.Data.SqlDbType.NVarChar}
        ] (personSelect 0)

    [<Test>]
    let ``where func property access``() =
        
        let f () = 
            { SubValue = {Value = "john" } }

        let q = fun (persons : IQueryable<Person>) -> 
            query {
                for p in persons do
                where(p.PersonName = f().SubValue.Value)
                select p
            }
        
        AreEqualDeleteOrSelectExpression q "SELECT T.PersonId, T.PersonName, T.JobKind, T.VersionNo " "FROM Person AS T WHERE (T.PersonName = @p1)" [
            {Name="@p1"; Value=("john"); DbType = System.Data.SqlDbType.NVarChar}
        ] (personSelect 0)

    [<Test>]
    let ``where local function applied``() =
        
        let f (gen : unit -> string) =
            let q = fun (persons : IQueryable<Person>) -> 
                query {
                    for p in persons do
                    where(p.PersonName = gen())
                    select p
                }
        
            AreEqualDeleteOrSelectExpression q "SELECT T.PersonId, T.PersonName, T.JobKind, T.VersionNo " "FROM Person AS T WHERE (T.PersonName = @p1)" [
                {Name="@p1"; Value=gen(); DbType = System.Data.SqlDbType.NVarChar}
            ] (personSelect(0))

        f (fun () -> "john")
        f (fun () -> "jane")

    [<Test>]
    let ``double where``() =
        let q = fun (persons : IQueryable<Person>) -> 
            query {
                for p in persons do
                where(p.PersonName = "john")
                where(p.PersonId = 5)
                select p
            }
        
        AreEqualDeleteOrSelectExpression q "SELECT T.PersonId, T.PersonName, T.JobKind, T.VersionNo " "FROM Person AS T WHERE (T.PersonName = @p1 AND T.PersonId = @p2)" [
            {Name="@p1"; Value="john"; DbType = System.Data.SqlDbType.NVarChar}
            {Name="@p2"; Value=5; DbType = System.Data.SqlDbType.Int}
        ] (personSelect 0)

    [<Test>]
    let ``where with single or``() =
        let q = fun (persons : IQueryable<Person>) -> 
            query {
                for p in persons do
                where(p.PersonName = "john" || p.PersonName = "doe")
                select p
            }
        
        AreEqualDeleteOrSelectExpression q "SELECT T.PersonId, T.PersonName, T.JobKind, T.VersionNo " "FROM Person AS T WHERE (T.PersonName = @p1 OR T.PersonName = @p2)" [
            {Name="@p1"; Value="john"; DbType = System.Data.SqlDbType.NVarChar}
            {Name="@p2"; Value="doe"; DbType = System.Data.SqlDbType.NVarChar}
        ] (personSelect 0)

    [<Test>]
    let ``where with two or``() =
        let q = fun (persons : IQueryable<Person>) -> 
            query {
                for p in persons do
                where(p.PersonName = "john" || p.PersonName = "doe" || p.PersonName = "james")
                select p
            }
        
        AreEqualDeleteOrSelectExpression q "SELECT T.PersonId, T.PersonName, T.JobKind, T.VersionNo " "FROM Person AS T WHERE (T.PersonName = @p1 OR T.PersonName = @p2 OR T.PersonName = @p3)" [
            {Name="@p1"; Value="john"; DbType = System.Data.SqlDbType.NVarChar}
            {Name="@p2"; Value="doe"; DbType = System.Data.SqlDbType.NVarChar}
            {Name="@p3"; Value="james"; DbType = System.Data.SqlDbType.NVarChar}
        ] (personSelect 0)

    [<Test>]
    let ``where option some``() =
        let q = fun (employees : IQueryable<Employee>) -> 
            query {
                for e in employees do
                where(e.DepartmentId = Some(1234))
                select e
            }
        
        AreEqualDeleteOrSelectExpression q "SELECT T.EmployeeId, T.EmployeeName, T.DepartmentId, T.VersionNo, T.PersonId " "FROM Employee AS T WHERE (T.DepartmentId = @p1)" [
            {Name="@p1"; Value=1234; DbType = System.Data.SqlDbType.Int}
        ] (employeeSelect 0)
    [<Test>]
    let ``where option none``() =
        let q = fun (employees : IQueryable<Employee>) -> 
            query {
                for e in employees do
                where(e.DepartmentId = None)
                select e
            }
        
        AreEqualDeleteOrSelectExpression q "SELECT T.EmployeeId, T.EmployeeName, T.DepartmentId, T.VersionNo, T.PersonId " "FROM Employee AS T WHERE (T.DepartmentId = @p1)" [
            {Name="@p1"; Value=System.DBNull.Value; DbType = System.Data.SqlDbType.Int}
        ] (employeeSelect 0)

    [<Test>]
    let ``where string contains``() =
        let q = fun (persons : IQueryable<Person>) -> 
            query {
                for p in persons do
                where(p.PersonName.Contains("john"))
                select p
            }
        
        AreEqualDeleteOrSelectExpression q "SELECT T.PersonId, T.PersonName, T.JobKind, T.VersionNo " "FROM Person AS T WHERE (T.PersonName LIKE '%' + @p1 + '%')" [
            {Name="@p1"; Value="john"; DbType = System.Data.SqlDbType.NVarChar}
        ] (personSelect 0)

    [<Test>]
    let ``where string startswith``() =
        let q = fun (persons : IQueryable<Person>) -> 
            query {
                for p in persons do
                where(p.PersonName.StartsWith("john"))
                select p
            }
        
        AreEqualDeleteOrSelectExpression q "SELECT T.PersonId, T.PersonName, T.JobKind, T.VersionNo " "FROM Person AS T WHERE (T.PersonName LIKE @p1 + '%')" [
            {Name="@p1"; Value="john"; DbType = System.Data.SqlDbType.NVarChar}
        ] (personSelect 0)

    [<Test>]
    let ``where string endswith``() =
        let q = fun (persons : IQueryable<Person>) -> 
            query {
                for p in persons do
                where(p.PersonName.EndsWith("john"))
                select p
            }
        
        AreEqualDeleteOrSelectExpression q "SELECT T.PersonId, T.PersonName, T.JobKind, T.VersionNo " "FROM Person AS T WHERE (T.PersonName LIKE '%' + @p1)" [
            {Name="@p1"; Value="john"; DbType = System.Data.SqlDbType.NVarChar}
        ] (personSelect 0)

    [<Test>]
    let ``where subquery contains id ``() =
        let q = fun (persons : IQueryable<Person>) -> 
            query {
                for p in persons do
                where(query {
                    for e in queryable<Employee>() do
                    select e.PersonId
                    contains p.PersonId
                })
                select p
            }
        
        AreEqualDeleteOrSelectExpression q "SELECT T.PersonId, T.PersonName, T.JobKind, T.VersionNo " "FROM Person AS T WHERE (T.PersonId IN (SELECT T2.PersonId FROM Employee AS T2))" [] (personSelect 0)

    [<Test>]
    let ``where subquery with where contains id ``() =
        let q = fun (persons : IQueryable<Person>) -> 
            query {
                for p in persons do
                where(query {
                    for e in queryable<Employee>() do
                    where(e.DepartmentId = Some(1234))
                    select e.PersonId
                    contains p.PersonId
                })
                select p
            }
        
        AreEqualDeleteOrSelectExpression q "SELECT T.PersonId, T.PersonName, T.JobKind, T.VersionNo " "FROM Person AS T WHERE (T.PersonId IN (SELECT T2.PersonId FROM Employee AS T2 WHERE (T2.DepartmentId = @p1)))" [
            {Name="@p1"; Value=1234; DbType = System.Data.SqlDbType.Int}
        ] (personSelect 0)

    [<Test>]
    let ``where subquery with where contains variable id``() =

        let departmentId = ref 1234
        let q = fun (persons : IQueryable<Person>) -> 
            query {
                for p in persons do
                where(query {
                    for e in queryable<Employee>() do
                    where(e.DepartmentId = Some(!departmentId))
                    select e.PersonId
                    contains p.PersonId
                })
                select p
            }
        
        AreEqualDeleteOrSelectExpression q "SELECT T.PersonId, T.PersonName, T.JobKind, T.VersionNo " "FROM Person AS T WHERE (T.PersonId IN (SELECT T2.PersonId FROM Employee AS T2 WHERE (T2.DepartmentId = @p1)))" [
            {Name="@p1"; Value=1234; DbType = System.Data.SqlDbType.Int}
        ] (personSelect(0))

    [<Test>]
    let ``select partial``() =
        let q = fun (persons : IQueryable<Person>) -> 
            query {
                for p in persons do
                select p.PersonName
            }
        
        AreEqualExpression q "SELECT T.PersonName FROM Person AS T" [] (stringSelect 0 Many)

    [<Test>]
    let ``partial select with where``() =
        let q = fun (persons : IQueryable<Person>) -> 
            query {
                for p in persons do
                where(p.PersonName = "john")
                select p.PersonName
            }
        
        AreEqualExpression q "SELECT T.PersonName FROM Person AS T WHERE (T.PersonName = @p1)" [
            {Name="@p1"; Value="john"; DbType = System.Data.SqlDbType.NVarChar}
        ] (stringSelect 0 Many)

    [<Test>]
    [<Ignore("Bug in fsharp compiler")>]
    let ``select partial tuple``() =
        ignore()
        //This is broken in the fsharp compiler.
        //https://github.com/Microsoft/visualfsharp/issues/47

//        let q = fun () -> 
//            query {
//                for p in queryable<Person>() do
//                select (p.PersonName, p.PersonId)
//            }
//        
//        AreEqualExpression q "SELECT T.PersonName, T.PersonId FROM Person AS T" [] 

    [<Test>]
    let ``count``() =
        
        let q = fun (persons : IQueryable<Person>) -> 
            query {
                for _p in persons do
                count
            }

        AreEqualExpression q "SELECT COUNT(*) FROM Person AS T" [] (simpleOneSelect typedefof<int> 0)

    [<Test>]
    let ``count where``() =
        let q = fun (persons : IQueryable<Person>) -> 
            query {
                for p in persons do
                where(p.PersonName = "john")
                count
            }

        AreEqualExpression q "SELECT COUNT(*) FROM Person AS T WHERE (T.PersonName = @p1)"  [
            {Name="@p1"; Value="john"; DbType = System.Data.SqlDbType.NVarChar}
        ] (simpleOneSelect typedefof<int> 0)

    [<Test>]
    let ``exists``() =
        
        let q = fun (persons : IQueryable<Person>) -> 
            query {
                for p in persons do
                exists(p.PersonName = "john")
            }

        AreEqualExpression q "SELECT CASE WHEN COUNT(*) > 0 THEN CAST(1 AS BIT) ELSE CAST(0 AS BIT) END FROM Person AS T WHERE (T.PersonName = @p1)"  [
            {Name="@p1"; Value="john"; DbType = System.Data.SqlDbType.NVarChar}
        ] (simpleOneSelect typedefof<bool> 0)

    [<Test>]
    let ``contains col``() =
        let q = fun (persons : IQueryable<Person>) -> 
            query {
                for p in persons do
                select p.PersonId
                contains 11
            }
        
        AreEqualExpression q "SELECT CASE WHEN COUNT(*) > 0 THEN CAST(1 AS BIT) ELSE CAST(0 AS BIT) END FROM Person AS T WHERE (T.PersonId = @p1)"  [
            {Name="@p1"; Value=11; DbType = System.Data.SqlDbType.Int}
        ] (simpleOneSelect typedefof<bool> 0)

    [<Test>]
    [<Ignore("Not implemented")>]
    let ``contains whole``() =
        let e = {
            PersonName = "john"
            JobKind = JobKind.Salesman
            VersionNo = 5
            PersonId = 10
        }

        let q = fun (persons : IQueryable<Person>) -> 
            query {
                for _p in persons do
                contains e
            }

        let sql = 
            ["SELECT CASE WHEN COUNT(*) > 0 THEN CAST(1 AS BIT) ELSE CAST(0 AS BIT) END FROM Person AS T WHERE ("]
            @ ["T.PersonName = @p1 AND "]
            @ ["T.JobKind = @p2 AND "]
            @ ["T.VersionNo = @p3 AND "]
            @ ["T.PersonId = @p4)"]

        AreEqualExpression q (sql |> String.concat("")) [
            {Name="@p1"; Value="john"; DbType = System.Data.SqlDbType.NVarChar}
            {Name="@p2"; Value=0; DbType = System.Data.SqlDbType.Int}
            {Name="@p3"; Value=5; DbType = System.Data.SqlDbType.Int}
            {Name="@p4"; Value=10; DbType = System.Data.SqlDbType.Int}
        ] (simpleOneSelect typedefof<bool> 0)

    [<Test>]
    let ``contains where``() =
        let q = fun (persons : IQueryable<Person>) -> 
            query {
                for p in persons do
                where(p.PersonName = "john")
                select p.PersonId
                contains 11
            }
        
        AreEqualExpression q "SELECT CASE WHEN COUNT(*) > 0 THEN CAST(1 AS BIT) ELSE CAST(0 AS BIT) END FROM Person AS T WHERE (T.PersonName = @p1 AND T.PersonId = @p2)" [
            {Name="@p1"; Value="john"; DbType = System.Data.SqlDbType.NVarChar}
            {Name="@p2"; Value=11; DbType = System.Data.SqlDbType.Int}
        ] (simpleOneSelect typedefof<bool> 0)

    [<Test>]
    let ``last throws``() =
        let q = fun (persons : IQueryable<Person>) -> 
            query {
                for _p in persons do
                last
            }
        
        let e = getExpression q
        let exc = Assert.Throws(fun () -> 
            QueryTranslator.translate QueryTranslator.SqlServer2012 QueryTranslator.SelectQuery None None None e |> ignore)
        Assert.AreEqual(exc.Message, "'last' operator has no translations for Sql Server")

    [<Test>]
    let ``lastOrDefault``() =
        let q = fun (persons : IQueryable<Person>) -> 
            query {
                for _p in persons do
                lastOrDefault
            }
        
        let e = getExpression q
        let exc = Assert.Throws(fun () -> 
            QueryTranslator.translate QueryTranslator.SqlServer2012 QueryTranslator.SelectQuery None None None e |> ignore)
        Assert.AreEqual(exc.Message, "'lastOrDefault' operator has no translations for Sql Server")

    [<Test>]
    let ``exactlyOne``() =
        let q = fun (persons : IQueryable<Person>) -> 
            query {
                for _p in persons do
                exactlyOne
            }
        
        AreEqualExpression q "SELECT TOP 2 T.PersonId, T.PersonName, T.JobKind, T.VersionNo FROM Person AS T" [] (personSelectType Single 0)

    [<Test>]
    let ``select exactlyOne partial``() =
        let q = fun (persons : IQueryable<Person>) -> 
            query {
                for p in persons do
                select(p.PersonId)
                exactlyOne
            }
        
        AreEqualExpression q "SELECT TOP 2 T.PersonId FROM Person AS T" [] (intSelect 0 Single)

    [<Test>]
    let ``exactlyOne where``() =
        let q = fun (persons : IQueryable<Person>) -> 
            query {
                for p in persons do
                where(p.PersonName = "john")
                exactlyOne
            }
        
        AreEqualExpression q "SELECT TOP 2 T.PersonId, T.PersonName, T.JobKind, T.VersionNo FROM Person AS T WHERE (T.PersonName = @p1)" [
            {Name="@p1"; Value="john"; DbType = System.Data.SqlDbType.NVarChar}
        ] (personSelectType Single 0)

    [<Test>]
    let ``exactlyOneOrDefault``() =
        let q = fun (persons : IQueryable<Person>) -> 
            query {
                for _p in persons do
                exactlyOneOrDefault
            }
        
        AreEqualExpression q "SELECT TOP 2 T.PersonId, T.PersonName, T.JobKind, T.VersionNo FROM Person AS T" [] (personSelectType SingleOrDefault 0)

    [<Test>]
    let ``select exactlyOneOrDefault partial``() =
        let q = fun (persons : IQueryable<Person>) -> 
            query {
                for p in persons do
                select(p.PersonId)
                exactlyOneOrDefault
            }
        
        AreEqualExpression q "SELECT TOP 2 T.PersonId FROM Person AS T" [] (intSelect 0 SingleOrDefault)

    [<Test>]
    let ``exactlyOneOrDefault where``() =
        let q = fun (persons : IQueryable<Person>) -> 
            query {
                for p in persons do
                where(p.PersonName = "john")
                exactlyOneOrDefault
            }
        
        AreEqualExpression q "SELECT TOP 2 T.PersonId, T.PersonName, T.JobKind, T.VersionNo FROM Person AS T WHERE (T.PersonName = @p1)" [
            {Name="@p1"; Value="john"; DbType = System.Data.SqlDbType.NVarChar}
        ] (personSelectType SingleOrDefault 0)

    [<Test>]
    let ``head``() =
        let q = fun (persons : IQueryable<Person>) -> 
            query {
                for _p in persons do
                head
            }
        
        AreEqualExpression q "SELECT TOP 1 T.PersonId, T.PersonName, T.JobKind, T.VersionNo FROM Person AS T" [] (personSelectType Single 0)

    [<Test>]
    let ``head where``() =
        let q = fun (persons : IQueryable<Person>) -> 
            query {
                for p in persons do
                where(p.PersonName = "john")
                head
            }
        
        AreEqualExpression q "SELECT TOP 1 T.PersonId, T.PersonName, T.JobKind, T.VersionNo FROM Person AS T WHERE (T.PersonName = @p1)" [
            {Name="@p1"; Value="john"; DbType = System.Data.SqlDbType.NVarChar}
        ] (personSelectType Single 0)

    [<Test>]
    let ``headOrDefault``() =
        let q = fun (persons : IQueryable<Person>) -> 
            query {
                for _p in persons do
                headOrDefault
            }
        
        AreEqualExpression q "SELECT TOP 1 T.PersonId, T.PersonName, T.JobKind, T.VersionNo FROM Person AS T" [] (personSelectType SingleOrDefault 0)

    [<Test>]
    let ``headOrDefault where``() =
        let q = fun (persons : IQueryable<Person>) -> 
            query {
                for p in persons do
                where(p.PersonName = "john")
                head
            }
        
        AreEqualExpression q "SELECT TOP 1 T.PersonId, T.PersonName, T.JobKind, T.VersionNo FROM Person AS T WHERE (T.PersonName = @p1)" [
            {Name="@p1"; Value="john"; DbType = System.Data.SqlDbType.NVarChar}
        ] (personSelectType Single 0)

    [<Test>]
    let ``minBy``() =
        let q = fun (persons : IQueryable<Person>) -> 
            query {
                for p in persons do
                minBy p.PersonId
            }
        
        AreEqualExpression q "SELECT TOP 1 T.PersonId FROM Person AS T ORDER BY T.PersonId ASC" [] (intSelect 0 Single)
    
    [<Test>]
    let ``minBy where``() =
        let q = fun (persons : IQueryable<Person>) -> 
            query {
                for p in persons do
                where(p.PersonName = "john")
                minBy p.PersonId
            }
        
        AreEqualExpression q "SELECT TOP 1 T.PersonId FROM Person AS T WHERE (T.PersonName = @p1) ORDER BY T.PersonId ASC" [
            {Name="@p1"; Value="john"; DbType = System.Data.SqlDbType.NVarChar}
        ] (intSelect 0 Single)

    [<Test>]
    let ``maxBy``() =
        let q = fun (persons : IQueryable<Person>) -> 
            query {
                for p in persons do
                maxBy p.PersonId
            }
        
        AreEqualExpression q "SELECT TOP 1 T.PersonId FROM Person AS T ORDER BY T.PersonId DESC" [] (intSelect 0 Single)
    
    [<Test>]
    let ``maxBy where``() =
        let q = fun (persons : IQueryable<Person>) -> 
            query {
                for p in persons do
                where(p.PersonName = "john")
                maxBy p.PersonId
            }
        
        AreEqualExpression q "SELECT TOP 1 T.PersonId FROM Person AS T WHERE (T.PersonName = @p1) ORDER BY T.PersonId DESC" [
            {Name="@p1"; Value="john"; DbType = System.Data.SqlDbType.NVarChar}
        ] (intSelect 0 Single)

    [<Test>]
    [<Ignore("Not implemented")>]
    let ``groupBy select count``() =
        ignore()
//        let q = fun () -> 
//            query {
//                for p in queryable<Person>() do
//                groupBy p.JobKind into g
//                select (g, query { for x in g do count })
//            }
//
//        AreEqualExpression q "SELECT T.PersonId, COUNT(*) FROM Person AS T GROUP BY T.PersonId" [] []

    [<Test>]
    [<Ignore("Not implemented")>]
    let ``select groupBy``() =
        ignore()
//        let q = fun () -> 
//            query {
//                for p in queryable<Person>() do
//                groupBy p.JobKind into g
//                select g
//            }
//            
//        AreEqualExpression q "SELECT T.PersonId, T.PersonName, T.JobKind, T.VersionNo FROM Person AS T" [] []
        //the group by must be done clientside

    [<Test>]
    [<Ignore("Not implemented")>]
    let ``select groupBy where``() =
        ignore()
//        let q = fun () -> 
//            query {
//                for p in queryable<Person>() do
//                where(p.PersonName = "john")
//                groupBy p.JobKind into g
//                select g
//            }
//            
//        AreEqualExpression q "SELECT T.PersonId, T.PersonName, T.JobKind, T.VersionNo FROM Person AS T WHERE (T.PersonName = @p1)" [
//            {Name="@p1"; Value="john"; DbType = System.Data.SqlDbType.NVarChar}
//        ] []
        //the group by must be done clientside

    [<Test>]
    let ``sortBy``() =
        let q = fun (persons : IQueryable<Person>) -> 
            query {
                for p in persons do
                sortBy p.PersonId
            }
            
        AreEqualExpression q "SELECT T.PersonId, T.PersonName, T.JobKind, T.VersionNo FROM Person AS T ORDER BY T.PersonId ASC" [] (personSelect 0)

    [<Test>]
    let ``sortBy thenBy``() =
        let q = fun (persons : IQueryable<Person>) -> 
            query {
                for p in persons do
                sortBy p.PersonId
                thenBy p.PersonName
            }
            
        AreEqualExpression q "SELECT T.PersonId, T.PersonName, T.JobKind, T.VersionNo FROM Person AS T ORDER BY T.PersonId ASC, T.PersonName ASC" [] (personSelect 0)

    [<Test>]
    let ``sortBy thenByDescending``() =
        let q = fun (persons : IQueryable<Person>) -> 
            query {
                for p in persons do
                sortBy p.PersonId
                thenByDescending p.PersonName
            }
            
        AreEqualExpression q "SELECT T.PersonId, T.PersonName, T.JobKind, T.VersionNo FROM Person AS T ORDER BY T.PersonId ASC, T.PersonName DESC" [] (personSelect 0)

    [<Test>]
    let ``sortBy where``() =
        let q = fun (persons : IQueryable<Person>) -> 
            query {
                for p in persons do
                sortBy p.PersonId
                where(p.PersonName = "john")
            }
            
        AreEqualExpression q "SELECT T.PersonId, T.PersonName, T.JobKind, T.VersionNo FROM Person AS T WHERE (T.PersonName = @p1) ORDER BY T.PersonId ASC" [
            {Name="@p1"; Value="john"; DbType = System.Data.SqlDbType.NVarChar}
        ] (personSelect 0)

    [<Test>]
    let ``sortByDescending``() =
        let q = fun (persons : IQueryable<Person>) -> 
            query {
                for p in persons do
                sortByDescending p.PersonId
            }
            
        AreEqualExpression q "SELECT T.PersonId, T.PersonName, T.JobKind, T.VersionNo FROM Person AS T ORDER BY T.PersonId DESC" [] (personSelect 0)

    [<Test>]
    let ``sortByDescending thenBy``() =
        let q = fun (persons : IQueryable<Person>) -> 
            query {
                for p in persons do
                sortByDescending p.PersonId
                thenBy p.PersonName
            }
            
        AreEqualExpression q "SELECT T.PersonId, T.PersonName, T.JobKind, T.VersionNo FROM Person AS T ORDER BY T.PersonId DESC, T.PersonName ASC" [] (personSelect 0)

    [<Test>]
    let ``sortByDescending thenByDescending``() =
        let q = fun (persons : IQueryable<Person>) -> 
            query {
                for p in persons do
                sortByDescending p.PersonId
                thenByDescending p.PersonName
            }
            
        AreEqualExpression q "SELECT T.PersonId, T.PersonName, T.JobKind, T.VersionNo FROM Person AS T ORDER BY T.PersonId DESC, T.PersonName DESC" [] (personSelect 0)

    [<Test>]
    let ``sortByDescending where``() =
        let q = fun (persons : IQueryable<Person>) -> 
            query {
                for p in persons do
                sortByDescending p.PersonId
                where(p.PersonName = "john")
            }
            
        AreEqualExpression q "SELECT T.PersonId, T.PersonName, T.JobKind, T.VersionNo FROM Person AS T WHERE (T.PersonName = @p1) ORDER BY T.PersonId DESC" [
            {Name="@p1"; Value="john"; DbType = System.Data.SqlDbType.NVarChar}
        ] (personSelect 0)


// To be implemented:        
//query {
//    for student in db.Student do
//    sumByNullable student.Age
//    }
//|> (fun sum -> printfn "Sum of ages: %s" (sum.Print()))
//
//query {
//    for student in db.Student do
//    minByNullable student.Age
//    }
//|> (fun age -> printfn "Minimum age: %s" (age.Print()))
//
//query {
//    for student in db.Student do
//    maxByNullable student.Age
//    }
//|> (fun age -> printfn "Maximum age: %s" (age.Print()))
//
//query {
//    for student in db.Student do
//    averageBy (float student.StudentID)
//    }
//|> printfn "Average student ID: %f"
// 
//query {
//    for student in db.Student do
//    averageByNullable (Nullable.float student.Age)
//    }
//|> (fun avg -> printfn "Average age: %s" (avg.Print()))
//
//query {
//    for student in db.Student do
//    find (student.Name = "Abercrombie, Kim")
//}
//|> (fun student -> printfn "Found a match with StudentID = %d" student.StudentID)
//
//query {
//    for student in db.Student do
//    all (SqlMethods.Like(student.Name, "%,%"))
//}
//|> printfn "Do all students have a comma in the name? %b"
//
//query {
//    for student in db.Student do
//    head
//    }
//|> (fun student -> printfn "Found the head student with StudentID = %d" student.StudentID)
//
//query {
//    for numbers in data do
//    nth 3
//    }
//|> printfn "Third number is %d"
//
//query {
//    for student in db.Student do
//    skip 1
//    }
//|> Seq.iter (fun student -> printfn "StudentID = %d" student.StudentID)
//
//query {
//    for number in data do
//    skipWhile (number < 3)
//    select number
//    }
//|> Seq.iter (fun number -> printfn "Number = %d" number)
//
//query {
//   for student in db.Student do
//   sumBy student.StudentID
//   }
//|> printfn "Sum of student IDs: %d" 
//
//query {
//   for student in db.Student do
//   select student
//   take 2
//   }
//|> Seq.iter (fun student -> printfn "StudentID = %d" student.StudentID)
//
//query {
//    for number in data do
//    takeWhile (number < 10)
//    }
//|> Seq.iter (fun number -> printfn "Number = %d" number)
//
//query {
//    for student in db.Student do
//    sortByNullable student.Age
//    select student
//}
//|> Seq.iter (fun student ->
//    printfn "StudentID, Name, Age: %d %s %s" student.StudentID student.Name (student.Age.Print()))
//
//query {
//    for student in db.Student do
//    sortByNullableDescending student.Age
//    select student
//}
//|> Seq.iter (fun student ->
//    printfn "StudentID, Name, Age: %d %s %s" student.StudentID student.Name (student.Age.Print()))
//
//query {
//    for student in db.Student do
//    sortBy student.Name
//    thenByNullable student.Age
//    select student
//}
//|> Seq.iter (fun student ->
//    printfn "StudentID, Name, Age: %d %s %s" student.StudentID student.Name (student.Age.Print()))
//
//query {
//    for student in db.Student do
//    sortBy student.Name
//    thenByNullableDescending student.Age
//    select student
//}
//|> Seq.iter (fun student ->
//    printfn "StudentID, Name, Age: %d %s %s" student.StudentID student.Name (student.Age.Print()))
//
//query {
//        for student in db.Student do
//        select student
//    }
//    |> Seq.iter (fun student -> printfn "%s %d %s" student.Name student.StudentID (student.Age.Print()))
//
//query {
//        for student in db.Student do        
//        count
//    }
//|>  (fun count -> printfn "Student count: %d" count)
//
//query {
//        for student in db.Student do
//        where (ExtraTopLevelOperators.query 
//                      { for courseSelection in db.CourseSelection do
//                        exists (courseSelection.StudentID = student.StudentID) })
//        select student }
//|> Seq.iter (fun student -> printfn "%A" student.Name)
//
//query {
//        for n in db.Student do
//        groupBy n.Age into g
//        select (g.Key, g.Count())
//}
//|> Seq.iter (fun (age, count) -> printfn "%s %d" (age.Print()) count)
//
//query {
//        for n in db.Student do
//        groupValBy n.Age n.Age into g
//        select (g.Key, g.Count())
//    }
//|> Seq.iter (fun (age, count) -> printfn "%s %d" (age.Print()) count)
//
//query {
//        for student in db.Student do
//        groupBy student.Age into g
//        where (g.Key.HasValue && g.Key.Value > 10)
//        select (g, g.Key)
//}
//|> Seq.iter (fun (students, age) ->
//    printfn "Age: %s" (age.Value.ToString())
//    students
//    |> Seq.iter (fun student -> printfn "%s" student.Name))
//
//query {
//        for student in db.Student do
//        groupBy student.Age into group
//        where (group.Count() > 1)
//        select (group.Key, group.Count())
//}
//|> Seq.iter (fun (age, ageCount) ->
//     printfn "Age: %s Count: %d" (age.Print()) ageCount)
//
//query {
//        for student in db.Student do
//        groupBy student.Age into g        
//        let total = query { for student in g do sumByNullable student.Age }
//        select (g.Key, g.Count(), total)
//}
//|> Seq.iter (fun (age, count, total) ->
//    printfn "Age: %d" (age.GetValueOrDefault())
//    printfn "Count: %d" count
//    printfn "Total years: %s" (total.ToString()))
//
//query {
//        for student in db.Student do
//        groupBy student.Age into g
//        where (g.Count() > 1)        
//        sortByDescending (g.Count())
//        select (g.Key, g.Count())
//}
//|> Seq.iter (fun (age, myCount) ->
//    printfn "Age: %s" (age.Print())
//    printfn "Count: %d" myCount)
//
//
//let idList = [1; 2; 5; 10]
//let idQuery = query { for id in idList do
//                       select id }
//query {
//        for student in db.Student do
//        where (idQuery.Contains(student.StudentID))
//        select student
//        }
//|> Seq.iter (fun student ->
//    printfn "Name: %s" student.Name)
//
//query {
//    for student in db.Student do
//    where (SqlMethods.Like( student.Name, "_e%") )
//    select student
//    take 2   
//    }
//|> Seq.iter (fun student -> printfn "%s" student.Name)
//
//query {
//    for student in db.Student do
//    where (SqlMethods.Like( student.Name, "[abc]%") )
//    select student  
//    }
//|> Seq.iter (fun student -> printfn "%s" student.Name)
//
//query {
//    for student in db.Student do
//    where (SqlMethods.Like( student.Name, "[^abc]%") )
//    select student  
//    }
//|> Seq.iter (fun student -> printfn "%s" student.Name)
//
//query {
//    for n in db.Student do
//    where (SqlMethods.Like( n.Name, "[^abc]%") )
//    select n.StudentID    
//    }
//|> Seq.iter (fun id -> printfn "%d" id)
//
//query {
//        for student in db.Student do
//        where (student.Name.Contains("a"))
//        select student
//    }
//|> Seq.iter (fun student -> printfn "%s" student.Name)
//
//let names = [|"a";"b";"c"|]
//query {
//    for student in db.Student do
//    if names.Contains (student.Name) then select student }
//|> Seq.iter (fun student -> printfn "%s" student.Name)
//
//query {
//        for student in db.Student do 
//        join selection in db.CourseSelection 
//          on (student.StudentID = selection.StudentID)
//        select (student, selection)
//    }
//|> Seq.iter (fun (student, selection) -> printfn "%d %s %d" student.StudentID student.Name selection.CourseID)
//
//query {
//    for student in db.Student do
//    leftOuterJoin selection in db.CourseSelection 
//      on (student.StudentID = selection.StudentID) into result
//    for selection in result.DefaultIfEmpty() do
//    select (student, selection)
//    }
//|> Seq.iter (fun (student, selection) ->
//    let selectionID, studentID, courseID =
//        match selection with
//        | null -> "NULL", "NULL", "NULL"
//        | sel -> (sel.ID.ToString(), sel.StudentID.ToString(), sel.CourseID.ToString())
//    printfn "%d %s %d %s %s %s" student.StudentID student.Name (student.Age.GetValueOrDefault()) selectionID studentID courseID)
//
//query {
//        for n in db.Student do 
//        join e in db.CourseSelection on (n.StudentID = e.StudentID)
//        count        
//    }
//|>  printfn "%d"
//
//query {
//        for student in db.Student do 
//        join selection in db.CourseSelection on (student.StudentID = selection.StudentID)
//        distinct        
//    }
//|> Seq.iter (fun (student, selection) -> printfn "%s %d" student.Name selection.CourseID)
//
//query {
//        for n in db.Student do 
//        join e in db.CourseSelection on (n.StudentID = e.StudentID)
//        distinct
//        count       
//    }
//|> printfn "%d"
//
//query {
//        for student in db.Student do
//        where (student.Age.Value >= 10 && student.Age.Value < 15)
//        select student
//    }
//|> Seq.iter (fun student -> printfn "%s" student.Name)
//
//query {
//        for student in db.Student do
//        where (student.Age.Value = 11 || student.Age.Value = 12)
//        select student
//    }
//|> Seq.iter (fun student -> printfn "%s" student.Name)
//
//query {
//        for n in db.Student do
//        where (n.Age.Value = 12 || n.Age.Value = 13)
//        sortByNullableDescending n.Age
//        select n
//    }
//|> Seq.iter (fun student -> printfn "%s %s" student.Name (student.Age.Print()))
//
//query {
//        for student in db.Student do
//        where ((student.Age.HasValue && student.Age.Value = 11) ||
//               (student.Age.HasValue && student.Age.Value = 12))
//        sortByDescending student.Name 
//        select student.Name
//        take 2
//    }
//|> Seq.iter (fun name -> printfn "%s" name)
//
//module Queries =
//    let query1 = query {
//            for n in db.Student do
//            select (n.Name, n.Age)
//        }
//
//    let query2 = query {
//            for n in db.LastStudent do
//            select (n.Name, n.Age)
//            }
//
//    query2.Union (query1)
//    |> Seq.iter (fun (name, age) -> printfn "%s %s" name (age.Print()))
//
//module Queries2 =
//    let query1 = query {
//           for n in db.Student do
//           select (n.Name, n.Age)
//        }
//
//    let query2 = query {
//            for n in db.LastStudent do
//            select (n.Name, n.Age)
//            }
//
//    query1.Intersect(query2)
//    |> Seq.iter (fun (name, age) -> printfn "%s %s" name (age.Print()))
//
//query {
//        for student in db.Student do
//        select (if student.Age.HasValue && student.Age.Value = -1 then
//                   (student.StudentID, System.Nullable<int>(100), student.Age)
//                else (student.StudentID, student.Age, student.Age))
//    }
//|> Seq.iter (fun (id, value, age) -> printfn "%d %s %s" id (value.Print()) (age.Print()))
//
//query {
//        for student in db.Student do
//        select (if student.Age.HasValue && student.Age.Value = -1 then
//                   (student.StudentID, System.Nullable<int>(100), student.Age)
//                elif student.Age.HasValue && student.Age.Value = 0 then
//                    (student.StudentID, System.Nullable<int>(100), student.Age)
//                else (student.StudentID, student.Age, student.Age))
//    }
//|> Seq.iter (fun (id, value, age) -> printfn "%d %s %s" id (value.Print()) (age.Print()))
//
//query {
//        for student in db.Student do
//        for course in db.Course do
//        select (student, course)
//}
//|> Seq.iteri (fun index (student, course) ->
//    if (index = 0) then printfn "StudentID Name Age CourseID CourseName"
//    printfn "%d %s %s %d %s" student.StudentID student.Name (student.Age.Print()) course.CourseID course.CourseName)
//
//query {
//    for student in db.Student do
//    join courseSelection in db.CourseSelection on
//        (student.StudentID = courseSelection.StudentID)
//    join course in db.Course on
//          (courseSelection.CourseID = course.CourseID)
//    select (student.Name, course.CourseName)
//    }
//    |> Seq.iter (fun (studentName, courseName) -> printfn "%s %s" studentName courseName)
//
//query {
//    for student in db.Student do
//    leftOuterJoin courseSelection in db.CourseSelection 
//      on (student.StudentID = courseSelection.StudentID) into g1
//    for courseSelection in g1.DefaultIfEmpty() do
//    leftOuterJoin course in db.Course 
//      on (courseSelection.CourseID = course.CourseID) into g2
//    for course in g2.DefaultIfEmpty() do
//    select (student.Name, course.CourseName)
//    }
//|> Seq.iter (fun (studentName, courseName) -> printfn "%s %s" studentName courseName)