using System.Text;

namespace CodeMaster.Application.Services.CodeGen;

/// <summary>
/// 开源版生成项目的报表打印占位实现。
/// 页面模板种子会为列表 / 详情页生成「打印」按钮并调用 openEntityPrintReport，
/// 而开源模板不含 <c>src/utils/printReport.js</c>（真实实现是私有文件），
/// 导致生成项目前端构建报错。这里提供一组同签名的空实现，保证构建可过。
/// </summary>
internal static class PrintReportStub
{
    private const string JavaScript = """
        /**
         * 报表打印入口 —— 开源版占位实现
         *
         * 页面模板种子会为列表页 / 详情页生成「打印」按钮，并调用
         * openEntityPrintReport。开源发行版不包含报表设计器与打印运行时，
         * 因此这里提供一组同签名的空实现，保证生成的项目可以直接构建运行。
         *
         * 完整版会由模板 overlay 提供真实实现；若文件已存在，导出 / 初始化流程不会覆盖它。
         * 请勿在此扩展业务逻辑：本文件的存在不代表具备报表能力。
         */

        /** 生成报表运行页地址（开源版无报表运行时，返回空地址） */
        export function printReportUrl() {
          return '';
        }

        /** 打开通用报表运行页（开源版空实现） */
        export function openPrintReport() {
          return null;
        }

        /** 按项目 / 模块打开打印报表（开源版空实现） */
        export async function openEntityPrintReport() {
          return null;
        }
        """;

    /// <summary>
    /// 写入开源版占位实现。文件已存在时跳过（完整版 overlay 注入的真实实现不会被覆盖）。
    /// </summary>
    public static async Task EnsureWrittenAsync(string vueUtilsDirectory)
    {
        var targetPath = Path.Combine(vueUtilsDirectory, "printReport.js");
        if (File.Exists(targetPath))
        {
            return;
        }

        Directory.CreateDirectory(vueUtilsDirectory);
        await File.WriteAllTextAsync(targetPath, JavaScript, new UTF8Encoding(false));
    }
}
