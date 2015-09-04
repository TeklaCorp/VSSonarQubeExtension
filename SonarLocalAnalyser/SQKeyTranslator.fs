﻿namespace SonarLocalAnalyser

open VSSonarPlugins.Types
open VSSonarPlugins
open System.IO
open System.Text

type KeyLookUpType =
   // the name
   | Module = 0
   | Flat = 1
   | VSBootStrapper = 2
   | Invalid = 3
   | ProjectGuid = 4

[<AllowNullLiteral>]
type SonarModule() = 
    // the name
    member val Name : string =  "" with get, set
    member val ProjectName : string = "" with get, set
    member val BaseDir : string = "" with get, set
    member val Sources : string = "" with get, set
    member val SubModules : SonarModule List = List.Empty with get, set
    member val ParentModule : SonarModule = null with get, set

[<AllowNullLiteral>]
type ISQKeyTranslator =     
  abstract member CreateConfiguration : file:string -> unit
  abstract member GetProjectKey : unit -> string 
  abstract member GetProjectName : unit -> string
  abstract member GetSources : unit -> string
  abstract member GetLookupType : unit -> KeyLookUpType
  abstract member SetLookupType : key:KeyLookUpType -> unit
  abstract member SetProjectKeyAndBaseDir : key:string * path:string -> unit
  abstract member SetProjectKey : key:string -> unit  
  abstract member GetModules : unit -> List<SonarModule>  
  abstract member TranslateKey : key:string * vshelper:IVsEnvironmentHelper * branch:string -> string
  abstract member TranslatePath : key:VsFileItem * vshelper:IVsEnvironmentHelper -> string
  

[<AllowNullLiteral>]
type SQKeyTranslator() = 
    let mutable projectKey : string = ""
    let mutable projectName : string = ""
    let mutable projectVersion : string = ""
    let mutable sonarSources : string = ""
    let mutable projectBaseDir : string = ""
    let mutable modules : SonarModule List = List.Empty

    let mutable lookupType : KeyLookUpType = KeyLookUpType.Invalid

    let rec SearchModule(modl : SonarModule, moduleName : string) =
        let mutable modReturn : SonarModule = null

        if modl.Name = moduleName then
            modReturn <- modl
        else
            
            for submod in modl.SubModules do
                if modReturn = null then
                    modReturn <- SearchModule(submod, moduleName)
        modReturn

    let getRelativeDirForModuleAgainstPrevious(currePath : string, moduleName : string) = 
        let mutable modl : SonarModule = null
        for submod in modules do
            if modl = null then
                modl <- SearchModule(submod, moduleName)

        if modl <> null then
            modl.BaseDir
        else
            currePath

    let UpdateModule(moduleName : string, baseDir : string, lines : string [], newModule : SonarModule) = 

        newModule.BaseDir <- baseDir
        let moduleData = lines |> Seq.tryFind (fun c -> c.Contains(moduleName + "sonar.projectName="))
        
        match moduleData with
        | Some value -> newModule.ProjectName <- value.Replace(moduleName + "sonar.projectName=", "").Trim()
        | _ -> ()

        let moduleData = lines |> Seq.tryFind (fun c -> c.Contains(moduleName + "sonar.projectBaseDir="))
        match moduleData with
        | Some value -> newModule.BaseDir <- Path.Combine(baseDir, value.Replace(moduleName + "sonar.projectBaseDir=", "").Trim())
        | _ -> ()

        let moduleData = lines |> Seq.tryFind (fun c -> c.Contains(moduleName + "sonar.sources="))
        match moduleData with
        | Some value -> newModule.Sources <- value.Replace(moduleName + "sonar.sources=", "").Trim()
        | _ -> ()

    let CreateKey(currentModule : SonarModule, fileItem : VsFileItem) = 
        let fileName = Path.GetFileName(fileItem.FilePath)
        let mutable builder = currentModule.Name + ":"

        let mutable parent = currentModule.ParentModule

        while parent <> null do
            builder <- parent.Name + ":" + builder
            parent <- parent.ParentModule

        builder + fileItem.FilePath.Replace(currentModule.BaseDir, "").TrimStart(Path.DirectorySeparatorChar).Replace(Path.DirectorySeparatorChar, '/')

    let rec searchFilePathinModules(currentModule : SonarModule, fileItem : VsFileItem) =
        
        let mutable moduleToRetrun : SonarModule = null

        if currentModule.SubModules.Length <> 0 then
            for submod in currentModule.SubModules do
                if moduleToRetrun = null then
                    moduleToRetrun <- searchFilePathinModules(submod, fileItem)
        else
            if currentModule.Sources <> "" then
                let normalizePath(path : string) =
                    Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar).ToLower()

                for source in currentModule.Sources.Split(',') do
                    if moduleToRetrun = null then
                        let BaseDirSRC = normalizePath(Path.Combine(currentModule.BaseDir, source))
                        let fileBaseParent = normalizePath(Directory.GetParent(fileItem.FilePath).ToString())
                        if BaseDirSRC.Equals(fileBaseParent) then
                            moduleToRetrun <- currentModule

        moduleToRetrun

    let rec ProcessModules(file : string, curreModule : SonarModule) =
        let based = Directory.GetParent(file).ToString()
        let lines = File.ReadAllLines(file)

        if curreModule <> null then 
            UpdateModule("", based, lines, curreModule)
        else
            projectBaseDir <- Directory.GetParent(file).ToString().TrimEnd(Path.DirectorySeparatorChar)

            let lineElem = lines |> Seq.tryFind (fun c -> c.Contains("sonar.visualstudio.enable=true"))
            match lineElem with
            | Some value -> lookupType <- KeyLookUpType.VSBootStrapper
            | _ -> ()

            let moduleData = lines |> Seq.tryFind (fun c -> c.Trim().StartsWith("sonar.projectKey="))
        
            match moduleData with
            | Some value -> projectKey <- value.Replace("sonar.projectKey=", "").Trim()
            | _ -> ()

            let moduleData = lines |> Seq.tryFind (fun c -> c.Trim().StartsWith("sonar.projectName="))
            match moduleData with
            | Some value -> projectName <- value.Replace("sonar.projectName=", "").Trim()
            | _ -> ()

            let moduleData = lines |> Seq.tryFind (fun c -> c.Trim().StartsWith("sonar.projectVersion="))
            match moduleData with
            | Some value -> projectVersion <- value.Replace("sonar.projectVersion=", "").Trim()
            | _ -> ()

            let moduleData = lines |> Seq.tryFind (fun c -> c.Trim().StartsWith("sonar.sources="))
            match moduleData with
            | Some value -> sonarSources <- value.Replace("sonar.sources=", "").Trim()
            | _ -> ()

        let lineElem = lines |> Seq.tryFind (fun c -> c.Contains("sonar.modules="))

        match lineElem with
        | Some line -> let modulesNames = line.Split('=').[1].Trim().Split(',')
                       if curreModule <> null then
                        curreModule.Sources <- ""

                       lookupType <- KeyLookUpType.Module
                       for modl in modulesNames do
                           let newModule = new SonarModule()
                           newModule.Name <- modl

                           if curreModule = null then
                                modules <- modules @ [newModule]
                           else
                                newModule.ParentModule <- curreModule
                                curreModule.SubModules <- curreModule.SubModules @ [newModule]

                           let moduleData = lines |> Seq.tryFind (fun c -> c.Contains(modl + ".sonar."))
                           match moduleData with
                           | Some value -> UpdateModule(modl + ".", based, lines, newModule)
                           | _ -> ProcessModules(Path.Combine(based, modl, "sonar-project.properties"), newModule)

        | _ -> if curreModule = null then
                    let lineElem = lines |> Seq.tryFind (fun c -> c.Contains("sonar.visualstudio.enable=true"))
                    match lineElem with
                    | Some value -> lookupType <- KeyLookUpType.VSBootStrapper
                    | _ -> ()
               else
                    UpdateModule("", based, lines, curreModule)

    interface ISQKeyTranslator with
        member this.SetProjectKeyAndBaseDir(key:string, path:string) =
            projectBaseDir <- path
            projectKey <- key
            ()

        member this.SetProjectKey(key:string) =
            projectKey <- key
            ()

        member this.CreateConfiguration(file : string) = 
            if File.Exists(file) then
                ProcessModules(file, null)
            ()

        member this.GetModules() = 
            modules

        member this.GetLookupType() = 
            lookupType

        member this.SetLookupType(typein : KeyLookUpType) = 
            lookupType <- typein
            
        member this.GetProjectKey() =
            projectKey

        member this.GetProjectName() =
            projectName

        member this.GetSources() =
            sonarSources

        member this.TranslatePath(fileItem : VsFileItem, vshelper : IVsEnvironmentHelper) =

            if lookupType = KeyLookUpType.Flat then
                let tounix = vshelper.GetProperFilePathCapitalization(fileItem.FilePath).Replace("\\", "/")
                let driveLetter = tounix.Substring(0, 1)
                let solutionCan = driveLetter + fileItem.Project.Solution.SolutionPath.Replace("\\", "/").Substring(1)
                let fromBaseDir = tounix.Replace(solutionCan + "/", "")

                if fileItem.Project.Solution.SonarProject <> null then
                    fileItem.Project.Solution.SonarProject.Key + ":" + fromBaseDir
                else
                    projectKey + ":" + fromBaseDir

            elif lookupType = KeyLookUpType.Module then
            
                let mutable keyOfResource = ""
                for moduled in modules do
                    if keyOfResource = "" then
                        let okMod = searchFilePathinModules(moduled, fileItem)
                        if okMod <> null then
                            keyOfResource <- 
                                if fileItem.Project.Solution.SonarProject <> null then
                                    fileItem.Project.Solution.SonarProject.Key + ":" + CreateKey(okMod, fileItem)
                                else
                                    projectKey + ":" + CreateKey(okMod, fileItem)
                keyOfResource

            elif lookupType = KeyLookUpType.VSBootStrapper then
                let filePath = fileItem.FilePath.Replace("\\", "/")
                let solutionPath = fileItem.Project.Solution.SolutionPath.Replace("\\", "/")
                let filerelativePath = filePath.Replace(solutionPath + "/", "")
                let keySplit = projectKey.Split(':').[0]

                if not(fileItem = null) then
                    let path = Directory.GetParent(fileItem.Project.ProjectFilePath).ToString().Replace("\\", "/")
                    let file = filePath.Replace(path + "/", "")
                    projectKey + ":" + fileItem.Project.ProjectName + ":" + file
                else
                    let filesplit = filerelativePath.Split('/')
                    projectKey + ":" + filesplit.[0] + ":" + filerelativePath.Replace(filesplit.[0] + "/", "")

            else
                ""

        member this.TranslateKey(key : string, vshelper : IVsEnvironmentHelper, branchIn:string) =
            let branch =
                if branchIn = "" || projectKey.Contains(branchIn) then
                    if projectKey.Contains(branchIn) then
                        ":"
                    else
                        ""
                else
                    ":" + branchIn + ":"

            let mutable path = ""

            if lookupType = KeyLookUpType.Invalid || lookupType = KeyLookUpType.ProjectGuid then

                path <- Path.Combine(projectBaseDir, key.Replace(projectKey + branch, "").Replace('/', Path.DirectorySeparatorChar))

                if File.Exists(path) then
                    lookupType <- KeyLookUpType.Flat
                else
                    path <-
                        let keyWithoutProjectKey = key.Replace(projectKey + branch, "")
                        let allModulesPresentInKey =  keyWithoutProjectKey.Split(':')
                        let modulesPresentInKey =  Array.sub allModulesPresentInKey 0 (allModulesPresentInKey.Length-1)

                        let mutable currPath = projectBaseDir
                        for moduleinKey in modulesPresentInKey do
                            currPath <- getRelativeDirForModuleAgainstPrevious(currPath, moduleinKey)

                        Path.Combine(currPath, allModulesPresentInKey.[allModulesPresentInKey.Length-1].Replace('/', Path.DirectorySeparatorChar))

                    if File.Exists(path) then
                        lookupType <- KeyLookUpType.Module
                    else
                        path <-
                            try
                                let keyWithoutProjectKey = key.Replace(projectKey + branch, "")
                                let allModulesPresentInKey =  keyWithoutProjectKey.Split(':')
            
                                let project = Directory.GetParent(vshelper.GetProjectByNameInSolution(allModulesPresentInKey.[0]).ProjectFilePath).ToString()
                                Path.Combine(project, allModulesPresentInKey.[1].Replace('/', Path.DirectorySeparatorChar))
                            with
                            | ex -> ""

                        if File.Exists(path) then
                            lookupType <- KeyLookUpType.Module
                        else
                            path <-
                                try
                                    let keyWithoutProjectKey = key.Replace(projectKey + branch, "")
                                    let allModulesPresentInKey =  keyWithoutProjectKey.Split(':')
            
                                    let project = Directory.GetParent(vshelper.GetProjectByGuidInSolution(allModulesPresentInKey.[0]).ProjectFilePath).ToString()
                                    Path.Combine(project, allModulesPresentInKey.[1].Replace('/', Path.DirectorySeparatorChar))
                                with
                                | ex -> ""

                            if File.Exists(path) then
                                lookupType <- KeyLookUpType.ProjectGuid
                            else
                                lookupType <- KeyLookUpType.Invalid
                   
            if lookupType = KeyLookUpType.Flat then
                Path.Combine(projectBaseDir, key.Replace(projectKey + branch, "").Replace('/', Path.DirectorySeparatorChar))
            elif lookupType = KeyLookUpType.Module then
                let keyWithoutProjectKey = key.Replace(projectKey + branch, "")
                let allModulesPresentInKey =  keyWithoutProjectKey.Split(':')
                let modulesPresentInKey =  Array.sub allModulesPresentInKey 0 (allModulesPresentInKey.Length-1)

                let mutable currPath = projectBaseDir
                for moduleinKey in modulesPresentInKey do
                    currPath <- getRelativeDirForModuleAgainstPrevious(currPath, moduleinKey)

                Path.Combine(currPath, allModulesPresentInKey.[allModulesPresentInKey.Length-1].Replace('/', Path.DirectorySeparatorChar))

            elif lookupType = KeyLookUpType.VSBootStrapper then
                let keyWithoutProjectKey = key.Replace(projectKey + branch, "")
                let allModulesPresentInKey =  keyWithoutProjectKey.Split(':')
            
                let project = Directory.GetParent(vshelper.GetProjectByNameInSolution(allModulesPresentInKey.[0]).ProjectFilePath).ToString()
                Path.Combine(project, allModulesPresentInKey.[1].Replace('/', Path.DirectorySeparatorChar))
            elif lookupType = KeyLookUpType.ProjectGuid then
                path

            else
            ""