namespace SyncExamSubJob.Models;

/// <summary>
/// The four synced tables, returned in parent -> child dependency order:
/// Employees -> Entity_roles -> Transactions -> Examination_Subjects.
///
/// Column names are identical between Oracle and SQL Server for all tables,
/// except Employees, where:
///   - EMY_AREA_CDE (Oracle only)            -> simply omitted (not listed here)
///   - EMY_EMAIL (SQL Server only)           -> LiteralNull (insert NULL, never updated)
///   - SIGNATURE_FILE_LINK (Oracle)          -> Mapped to SQL EMY_SIGNATURE_FILE_LINK
/// </summary>
public static class TableDefinitions
{
    public static IReadOnlyList<TableDefinition> InDependencyOrder { get; } = new[]
    {
        Employees(),
        EntityRoles(),
        Transactions(),
        ExaminationSubjects(),
    };

    private static TableDefinition Employees() => new(
        name: "Employees",
        sqlTable: "Employees",
        oracleTable: "EMPLOYEES",
        primaryKey: "EMY_ID",
        createDtmColumn: "EMY_CREATE_DTM",
        modifyDtmColumn: "EMY_MDFY_DTM",
        dependencies: Array.Empty<string>(),
        columns: new[]
        {
            ColumnDef.Key("EMY_ID"),
            ColumnDef.Col("EMY_CDE"),
            ColumnDef.Col("EMY_USERID"),
            ColumnDef.Col("EMY_EMSN_CDE"),
            ColumnDef.Col("EMY_PSTP_CDE"),
            ColumnDef.Col("EMY_ACTV_IND"),
            ColumnDef.Col("EMY_FIRST_NM"),
            ColumnDef.Col("EMY_LAST_NM"),
            ColumnDef.Col("EMY_CLS_TITLE"),
            ColumnDef.Col("EMY_CORRS_NAME"),
            ColumnDef.Col("EMY_CORRS_TITLE"),
            ColumnDef.Col("EMY_INFRML_NM"),
            ColumnDef.Col("EMY_LCL_PHN_NUM"),
            ColumnDef.Col("EMY_MIDDLE_NM"),
            ColumnDef.Col("EMY_WRK_END_TIME"),
            ColumnDef.Col("EMY_WRK_STRT_TIME"),
            ColumnDef.LiteralNull("EMY_EMAIL"),                                  // SQL-only
            ColumnDef.Mapped("EMY_SIGNATURE_FILE_LINK", "SIGNATURE_FILE_LINK"),  // renamed
            ColumnDef.Col("EMY_CREATE_DTM"),
            ColumnDef.Col("EMY_CREATE_USR"),
            ColumnDef.Col("EMY_MDFY_DTM"),
            ColumnDef.Col("EMY_MDFY_USR"),
        });

    private static TableDefinition EntityRoles() => new(
        name: "Entity_roles",
        sqlTable: "Entity_roles",
        oracleTable: "ENTITY_ROLES",
        primaryKey: "ENRL_ID",
        createDtmColumn: "ENRL_CREATE_DTM",
        modifyDtmColumn: "ENRL_MDFY_DTM",
        dependencies: new[] { "Employees" },
        columns: new[]
        {
            ColumnDef.Key("ENRL_ID"),
            ColumnDef.Col("ENRL_EMY_CDE"),
            ColumnDef.Col("ENRL_ENRP_CDE"),
            ColumnDef.Col("ENRL_ORTE_CDE"),
            ColumnDef.Col("ENRL_SSPCTD_ENRP_CDE"),
            ColumnDef.Col("ENRL_STE_CDE"),
            ColumnDef.Col("ENRL_CREATE_DTM"),
            ColumnDef.Col("ENRL_CREATE_USR"),
            ColumnDef.Col("ENRL_FED_CVRD_IND"),
            ColumnDef.Col("ENRL_LAST_NM"),
            ColumnDef.Col("ENRL_RGSTR_STAT_CDE"),
            ColumnDef.Col("ENRL_BIRTH_DT"),
            ColumnDef.Col("ENRL_CRD_NUM"),
            ColumnDef.Col("ENRL_DBA_NM"),
            ColumnDef.Col("ENRL_ENTY_FILE_NUM"),
            ColumnDef.Col("ENRL_NM_PREFIX"),
            ColumnDef.Col("ENRL_NM_SUFFIX"),
            ColumnDef.Col("ENRL_FILING_TYP_CDE"),
            ColumnDef.Col("ENRL_EXPIRE_DT"),
            ColumnDef.Col("ENRL_FIRST_NM"),
            ColumnDef.Col("ENRL_FSCL_YREND_DT"),
            ColumnDef.Col("ENRL_MDFY_DTM"),
            ColumnDef.Col("ENRL_MDFY_USR"),
            ColumnDef.Col("ENRL_MIDDLE_NM"),
            ColumnDef.Col("ENRL_RGSTR_DT"),
            ColumnDef.Col("ENRL_TAX_NUM"),
            ColumnDef.Col("ENRL_INVSTG_SCTP_NM"),
            ColumnDef.Col("ENRL_EXMPT_IND"),
            ColumnDef.Col("ENRL_EXMPT_DT"),
            ColumnDef.Col("ENRL_ENTY_SBST_CDE"),
            ColumnDef.Col("ENRL_TERMINATE_DT"),
            ColumnDef.Col("ENRL_SSPCTD_SECY_CDE"),
            ColumnDef.Col("ENRL_TAXID_FMT"),
            ColumnDef.Col("ENRL_ERA_IND"),
        });

    private static TableDefinition Transactions() => new(
        name: "Transactions",
        sqlTable: "Transactions",
        oracleTable: "TRANSACTIONS",
        primaryKey: "TRN_ID",
        createDtmColumn: "TRN_CREATE_DTM",
        modifyDtmColumn: "TRN_MDFY_DTM",
        dependencies: new[] { "Entity_roles", "Employees" },
        columns: new[]
        {
            ColumnDef.Key("TRN_ID"),
            ColumnDef.Col("TRN_AGOR_ID"),
            ColumnDef.Col("TRN_BDOC_ID"),
            ColumnDef.Col("TRN_BDRP_ID"),
            ColumnDef.Col("TRN_DSTE_ID"),
            ColumnDef.Col("TRN_EMY_CDE"),
            ColumnDef.Col("TRN_ENRL_ID"),
            ColumnDef.Col("TRN_ENRP_CDE"),
            ColumnDef.Col("TRN_INN_FILE_NBR"),
            ColumnDef.Col("TRN_QSST_ID"),
            ColumnDef.Col("TRN_TRTP_CDE"),
            ColumnDef.Col("TRN_OPEN_DT"),
            ColumnDef.Col("TRN_TRN_FILE_NUM"),
            ColumnDef.Col("TRN_CLOSING_DT"),
            ColumnDef.Col("TRN_BTRG_ID"),
            ColumnDef.Col("TRN_OPEN_IND"),
            ColumnDef.Col("TRN_PRCS_TYP_CDE"),
            ColumnDef.Col("TRN_DISP_DT"),
            ColumnDef.Col("TRN_CREATE_DTM"),
            ColumnDef.Col("TRN_CREATE_USR"),
            ColumnDef.Col("TRN_MDFY_DTM"),
            ColumnDef.Col("TRN_MDFY_USR"),
        });

    private static TableDefinition ExaminationSubjects() => new(
        name: "Examination_Subjects",
        sqlTable: "Examination_Subjects",
        oracleTable: "EXAMINATION_SUBJECTS",
        primaryKey: "EXST_ID",
        createDtmColumn: "EXST_CREATE_DTM",
        modifyDtmColumn: "EXST_MDFY_DTM",
        dependencies: new[] { "Entity_roles", "Transactions" },
        columns: new[]
        {
            ColumnDef.Key("EXST_ID"),
            ColumnDef.Col("EXST_ENRL_ID"),
            ColumnDef.Col("EXST_TRN_ID"),
            ColumnDef.Col("EXST_TRGT_SBJT_IND"),
            ColumnDef.Col("EXST_CASE_NUM"),
            ColumnDef.Col("EXST_CREATE_DTM"),
            ColumnDef.Col("EXST_CREATE_USR"),
            ColumnDef.Col("EXST_MDFY_DTM"),
            ColumnDef.Col("EXST_MDFY_USR"),
        });
}
