This is a command line utility to generate PGP-encrypted XML files 
Call ./UkrdcPgpXml.exe --SFID=1 
-  SFID is the sending facility Id as defined in table SendingFacilities
-  The XML is pulled from the database by a scalar function (name and parameters supplied by appsettings.json)
-  The  namespace, additional nodes and attributes required by UKRDC are added to the XML
-  The XML is encrypted by PGP using a public key (path is supplied by appsettings.json) 
-  The encrypted XML is saved to an output folder (path is supplied by appsettings.json)
-  One XML file is generated for each patient in the list returned by table-valued function dbo.PatientsToExport(@sendingFacilityId)
-  table Submissions and table-valued function PatientsToExport(.. supply other controlling parameters.
-  For testing, the key file may be locally generated to include a private key so the files can be decrypted using PGP
    - otherwise use the key file provided by UKRR, which includes only the UKRR's public key. 

It requires access to a database with the following entry points
  - scalar function returning XML with the following parameters (names provided in the appsettings.json file);
    - sending facility Id
    - patient Id
    - start date //earliest date for lab results and observations
    - stop date //latest date for lab results and observations
    
  - table dbo.Submissions(Id int IDENTITY(1,1) PRIMARY KEY, SendingFacilityId int NOT NULL, Start datetime NOT NULL,
  	Stop datetime NOT NULL, PopulatedTables datetime NOT NULL, GeneratedXml datetime NULL, Submitted datetime NULL,
  	NPatients int NULL)  //populated when patient tables have been populated, supplies start and stop parameter to the XML-function and the <sendingFacility time= .. from PopulatedTables

  - table dbo.SendingFacilities(Id smallint PRIMARY KEY, Name varchar(255) NOT NULL, Code varchar(255) NOT NULL)

  - table-valued function dbo.PatientsToExport(@sendingFacilityId) returning list of patients to export (Id int, Identifier char(10))



    
